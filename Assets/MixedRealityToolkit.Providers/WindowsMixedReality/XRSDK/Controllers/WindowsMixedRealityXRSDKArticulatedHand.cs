﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit.Utilities;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

#if WINDOWS_UWP
using Microsoft.MixedReality.Toolkit.WindowsMixedReality;
#if WMR_ENABLED
using UnityEngine.XR.WindowsMR;
#endif // WMR_ENABLED
using Windows.Perception.People;
using Windows.UI.Input.Spatial;
#endif // WINDOWS_UWP

namespace Microsoft.MixedReality.Toolkit.XRSDK.WindowsMixedReality
{
    /// <summary>
    /// XR SDK implementation of Windows Mixed Reality articulated hands.
    /// </summary>
    [MixedRealityController(
        SupportedControllerType.ArticulatedHand,
        new[] { Handedness.Left, Handedness.Right })]
    public class WindowsMixedRealityXRSDKArticulatedHand : BaseWindowsMixedRealityXRSDKSource, IMixedRealityHand
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        public WindowsMixedRealityXRSDKArticulatedHand(TrackingState trackingState, Handedness controllerHandedness, IMixedRealityInputSource inputSource = null, MixedRealityInteractionMapping[] interactions = null)
            : base(trackingState, controllerHandedness, inputSource, interactions) { }

        /// <inheritdoc />
        public override MixedRealityInteractionMapping[] DefaultInteractions => new[]
        {
            new MixedRealityInteractionMapping(0, "Spatial Pointer", AxisType.SixDof, DeviceInputType.SpatialPointer),
            new MixedRealityInteractionMapping(1, "Spatial Grip", AxisType.SixDof, DeviceInputType.SpatialGrip),
            new MixedRealityInteractionMapping(2, "Select", AxisType.Digital, DeviceInputType.Select),
            new MixedRealityInteractionMapping(3, "Grab", AxisType.SingleAxis, DeviceInputType.TriggerPress),
            new MixedRealityInteractionMapping(4, "Index Finger Pose", AxisType.SixDof, DeviceInputType.IndexFinger)
        };

        private static readonly HandFinger[] handFingers = Enum.GetValues(typeof(HandFinger)) as HandFinger[];
        private readonly List<Bone> fingerBones = new List<Bone>();
        private readonly Dictionary<TrackedHandJoint, MixedRealityPose> unityJointPoses = new Dictionary<TrackedHandJoint, MixedRealityPose>();

#if WINDOWS_UWP
        private HandMeshObserver handMeshObserver = null;
        private int[] handMeshTriangleIndices = null;
        private bool hasRequestedHandMeshObserver = false;
        private Vector2[] handMeshUVs;
        private readonly List<object> states = new List<object>();

        protected void InitializeUVs(Vector3[] neutralPoseVertices)
        {
            if (neutralPoseVertices.Length == 0)
            {
                Debug.LogError("Loaded 0 verts for neutralPoseVertices");
            }

            float minY = neutralPoseVertices[0].y;
            float maxY = minY;

            for (int ix = 1; ix < neutralPoseVertices.Length; ix++)
            {
                Vector3 p = neutralPoseVertices[ix];

                if (p.y < minY)
                {
                    minY = p.y;
                }
                else if (p.y > maxY)
                {
                    maxY = p.y;
                }
            }

            float scale = 1.0f / (maxY - minY);

            handMeshUVs = new Vector2[neutralPoseVertices.Length];

            for (int ix = 0; ix < neutralPoseVertices.Length; ix++)
            {
                Vector3 p = neutralPoseVertices[ix];

                handMeshUVs[ix] = new Vector2(p.x * scale + 0.5f, (p.y - minY) * scale);
            }
        }

        private async void SetHandMeshObserver(SpatialInteractionSourceState sourceState)
        {
            handMeshObserver = await sourceState.Source.TryCreateHandMeshObserverAsync();
        }
#endif // WINDOWS_UWP

        #region Update data functions

        /// <inheritdoc />
        public override void UpdateController(InputDevice inputDevice)
        {
            if (!Enabled) { return; }

            base.UpdateController(inputDevice);

            UpdateHandData(inputDevice);

            for (int i = 0; i < Interactions?.Length; i++)
            {
                switch (Interactions[i].InputType)
                {
                    case DeviceInputType.IndexFinger:
                        UpdateIndexFingerData(Interactions[i]);
                        break;
                }
            }
        }

        /// <summary>
        /// Update the hand data from the device.
        /// </summary>
        /// <param name="interactionSourceState">The InteractionSourceState retrieved from the platform.</param>
        private void UpdateHandData(InputDevice inputDevice)
        {
            Hand hand;
            if (inputDevice.TryGetFeatureValue(CommonUsages.handData, out hand))
            {
                foreach (HandFinger finger in handFingers)
                {
                    if (hand.TryGetFingerBones(finger, fingerBones))
                    {
                        for (int i = 0; i < fingerBones.Count; i++)
                        {
                            TrackedHandJoint trackedHandJoint = ConvertToTrackedHandJoint(finger, i);
                            Bone bone = fingerBones[i];

                            Vector3 position = Vector3.zero;
                            Quaternion rotation = Quaternion.identity;

                            if (bone.TryGetPosition(out position) || bone.TryGetRotation(out rotation))
                            {
                                // We want input sources to follow the Playspace, so fold in the playspace transform here to
                                // put the controller pose into world space.
                                position = MixedRealityPlayspace.TransformPoint(position);
                                rotation = MixedRealityPlayspace.Rotation * rotation;

                                unityJointPoses[trackedHandJoint] = new MixedRealityPose(position, rotation);
                            }
                        }

                        // Unity doesn't provide a palm joint, so we synthesize one here
                        unityJointPoses[TrackedHandJoint.Palm] = CurrentControllerPose;
                    }
                }

                CoreServices.InputSystem?.RaiseHandJointsUpdated(InputSource, ControllerHandedness, unityJointPoses);
            }

#if WINDOWS_UWP
            MixedRealityHandTrackingProfile handTrackingProfile = null;
            MixedRealityInputSystemProfile inputSystemProfile = CoreServices.InputSystem?.InputSystemProfile;
            if (inputSystemProfile != null)
            {
                handTrackingProfile = inputSystemProfile.HandTrackingProfile;
            }

            if (handTrackingProfile == null || !handTrackingProfile.EnableHandMeshVisualization)
            {
                // if hand mesh visualization is disabled make sure to destroy our hand mesh observer if it has already been created
                if (handMeshObserver != null)
                {
                    // Notify that hand mesh has been updated (cleared)
                    HandMeshInfo handMeshInfo = new HandMeshInfo();
                    CoreServices.InputSystem?.RaiseHandMeshUpdated(InputSource, ControllerHandedness, handMeshInfo);
                    hasRequestedHandMeshObserver = false;
                    handMeshObserver = null;
                }
                return;
            }

#if WMR_ENABLED
            XRSDKSubsystemHelpers.InputSubsystem?.GetCurrentSourceStates(states);
#endif // WMR_ENABLED

            foreach (SpatialInteractionSourceState sourceState in states)
            {
                if (sourceState.Source.Handedness.ToMRTKHandedness() == ControllerHandedness)
                {
                    HandPose handPose = sourceState.TryGetHandPose();

                    // Accessing the hand mesh data involves copying quite a bit of data, so only do it if application requests it.
                    if (handMeshObserver == null && !hasRequestedHandMeshObserver)
                    {
                        SetHandMeshObserver(sourceState);
                        hasRequestedHandMeshObserver = true;
                    }

                    if (handMeshObserver != null && handMeshTriangleIndices == null)
                    {
                        uint indexCount = handMeshObserver.TriangleIndexCount;
                        ushort[] indices = new ushort[indexCount];
                        handMeshObserver.GetTriangleIndices(indices);
                        handMeshTriangleIndices = new int[indexCount];
                        Array.Copy(indices, handMeshTriangleIndices, (int)handMeshObserver.TriangleIndexCount);

                        // Compute neutral pose
                        Vector3[] neutralPoseVertices = new Vector3[handMeshObserver.VertexCount];
                        HandPose neutralPose = handMeshObserver.NeutralPose;
                        var vertexAndNormals = new HandMeshVertex[handMeshObserver.VertexCount];
                        HandMeshVertexState handMeshVertexState = handMeshObserver.GetVertexStateForPose(neutralPose);
                        handMeshVertexState.GetVertices(vertexAndNormals);

                        for (int i = 0; i < handMeshObserver.VertexCount; i++)
                        {
                            neutralPoseVertices[i] = vertexAndNormals[i].Position.ToUnityVector3();
                        }

                        // Compute UV mapping
                        InitializeUVs(neutralPoseVertices);
                    }

                    if (handPose != null && handMeshObserver != null && handMeshTriangleIndices != null)
                    {
                        var vertexAndNormals = new HandMeshVertex[handMeshObserver.VertexCount];
                        var handMeshVertexState = handMeshObserver.GetVertexStateForPose(handPose);
                        handMeshVertexState.GetVertices(vertexAndNormals);

                        var meshTransform = handMeshVertexState.CoordinateSystem.TryGetTransformTo(WindowsMixedRealityUtilities.SpatialCoordinateSystem);
                        if (meshTransform.HasValue)
                        {
                            System.Numerics.Vector3 scale;
                            System.Numerics.Quaternion rotation;
                            System.Numerics.Vector3 translation;
                            System.Numerics.Matrix4x4.Decompose(meshTransform.Value, out scale, out rotation, out translation);

                            var handMeshVertices = new Vector3[handMeshObserver.VertexCount];
                            var handMeshNormals = new Vector3[handMeshObserver.VertexCount];

                            for (int i = 0; i < handMeshObserver.VertexCount; i++)
                            {
                                handMeshVertices[i] = vertexAndNormals[i].Position.ToUnityVector3();
                                handMeshNormals[i] = vertexAndNormals[i].Normal.ToUnityVector3();
                            }

                            HandMeshInfo handMeshInfo = new HandMeshInfo
                            {
                                vertices = handMeshVertices,
                                normals = handMeshNormals,
                                triangles = handMeshTriangleIndices,
                                uvs = handMeshUVs,
                                position = translation.ToUnityVector3(),
                                rotation = rotation.ToUnityQuaternion()
                            };

                            CoreServices.InputSystem?.RaiseHandMeshUpdated(InputSource, ControllerHandedness, handMeshInfo);
                        }
                    }
                }
            }
#endif // WINDOWS_UWP
        }

        private void UpdateIndexFingerData(MixedRealityInteractionMapping interactionMapping)
        {
            // Update the interaction data source
            interactionMapping.PoseData = unityJointPoses[TrackedHandJoint.IndexTip];

            // If our value changed raise it.
            if (interactionMapping.Changed)
            {
                // Raise input system event if it's enabled
                CoreServices.InputSystem?.RaisePoseInputChanged(InputSource, ControllerHandedness, interactionMapping.MixedRealityInputAction, interactionMapping.PoseData);
            }
        }

        /// <summary>
        /// Converts a Unity finger bone into an MRTK hand joint.
        /// </summary>
        /// <remarks>
        /// For HoloLens 2, Unity provides four joints per finger, in index order of metacarpal (0) to tip (4).
        /// The first joint for the thumb is the wrist joint. Palm joint is not provided.
        /// </remarks>
        /// <param name="finger">The Unity classification of the current finger.</param>
        /// <param name="index">The Unity index of the current finger bone.</param>
        /// <returns>The current Unity finger bone converted into an MRTK joint.</returns>
        private TrackedHandJoint ConvertToTrackedHandJoint(HandFinger finger, int index)
        {
            switch (finger)
            {
                case HandFinger.Thumb: return (index == 0) ? TrackedHandJoint.Wrist : TrackedHandJoint.ThumbMetacarpalJoint + index - 1;
                case HandFinger.Index: return TrackedHandJoint.IndexMetacarpal + index;
                case HandFinger.Middle: return TrackedHandJoint.MiddleMetacarpal + index;
                case HandFinger.Ring: return TrackedHandJoint.RingMetacarpal + index;
                case HandFinger.Pinky: return TrackedHandJoint.PinkyMetacarpal + index;
                default: return TrackedHandJoint.None;
            }
        }

        #region IMixedRealityHand Implementation

        /// <inheritdoc/>
        public bool TryGetJoint(TrackedHandJoint joint, out MixedRealityPose pose) => unityJointPoses.TryGetValue(joint, out pose);

        #endregion IMixedRealityHand Implementation

        private readonly float CursorBeamBackwardTolerance = 0.5f;
        private readonly float CursorBeamUpTolerance = 0.8f;

        /// <inheritdoc/>
        public override bool IsInPointingPose
        {
            get
            {
                bool valid = true;
                MixedRealityPose palmJoint;
                if (unityJointPoses.TryGetValue(TrackedHandJoint.Palm, out palmJoint))
                {
                    Vector3 palmNormal = palmJoint.Rotation * (-1 * Vector3.up);
                    if (CursorBeamBackwardTolerance >= 0)
                    {
                        Vector3 cameraBackward = -CameraCache.Main.transform.forward;
                        if (Vector3.Dot(palmNormal.normalized, cameraBackward) > CursorBeamBackwardTolerance)
                        {
                            valid = false;
                        }
                    }
                    if (valid && CursorBeamUpTolerance >= 0)
                    {
                        if (Vector3.Dot(palmNormal, Vector3.up) > CursorBeamUpTolerance)
                        {
                            valid = false;
                        }
                    }
                }
                return valid;
            }
        }

        #endregion Update data functions
    }
}