/*
 * @author Michael Holub
 * @author Valentin Simonov / http://va.lent.in/
 */

using System;
using System.Collections.Generic;
using TouchScript.Pointers;
using TouchScript.Utils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Profiling;
using UnityInput;

namespace TouchScript.InputSources.InputHandlers
{
    /// <summary>
    /// Unity touch handling implementation which can be embedded and controlled from other (input) classes.
    /// </summary>
    public class TouchHandler : IInputHandler, IDisposable
    {
        #region Public properties

        /// <inheritdoc />
        public ICoordinatesRemapper CoordinatesRemapper { get; set; }

        /// <summary>
        /// Gets a value indicating whether there any active pointers.
        /// </summary>
        /// <value> <c>true</c> if this instance has active pointers; otherwise, <c>false</c>. </value>
        public bool HasPointers
        {
            get { return pointersNum > 0; }
        }

        #endregion

        #region Private variables

#if ENABLE_INPUT_SYSTEM
        private PointerControls _controls;
#endif

        private IInputSource input;
        private PointerDelegate addPointer;
        private PointerDelegate updatePointer;
        private PointerDelegate pressPointer;
        private PointerDelegate releasePointer;
        private PointerDelegate removePointer;
        private PointerDelegate cancelPointer;

        private ObjectPool<TouchPointer> touchPool;
        // Unity fingerId -> TouchScript touch info
        private Dictionary<int, TouchState> systemToInternalId = new Dictionary<int, TouchState>(10);
        private int pointersNum;

#if UNITY_5_6_OR_NEWER
        private CustomSampler updateSampler;
#endif

        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="TouchHandler" /> class.
        /// </summary>
        /// <param name="input">An input source to init new pointers with.</param>
        /// <param name="addPointer">A function called when a new pointer is detected.</param>
        /// <param name="updatePointer">A function called when a pointer is moved or its parameter is updated.</param>
        /// <param name="pressPointer">A function called when a pointer touches the surface.</param>
        /// <param name="releasePointer">A function called when a pointer is lifted off.</param>
        /// <param name="removePointer">A function called when a pointer is removed.</param>
        /// <param name="cancelPointer">A function called when a pointer is cancelled.</param>
        public TouchHandler(IInputSource input, PointerDelegate addPointer, PointerDelegate updatePointer, PointerDelegate pressPointer, PointerDelegate releasePointer, PointerDelegate removePointer, PointerDelegate cancelPointer)
        {
            this.input = input;
            this.addPointer = addPointer;
            this.updatePointer = updatePointer;
            this.pressPointer = pressPointer;
            this.releasePointer = releasePointer;
            this.removePointer = removePointer;
            this.cancelPointer = cancelPointer;

            touchPool = new ObjectPool<TouchPointer>(10, newPointer, null, resetPointer, "TouchHandler/Touch");
            touchPool.Name = "Touch";

#if ENABLE_INPUT_SYSTEM
            _controls = new PointerControls();
            _controls.pointer.point.started += OnPointerActionStarted;
            _controls.pointer.point.performed += OnPointerAction;
            _controls.pointer.point.canceled += OnPointerActionCanceled;
#endif

#if UNITY_5_6_OR_NEWER
            updateSampler = CustomSampler.Create("[TouchScript] Update Touch");
#endif
        }

#if ENABLE_INPUT_SYSTEM
        UnityInput.Gestures.PointerInput retrievePointerInput(InputAction.CallbackContext context)
        {
            var control = context.control;
            var device = control.device;

            // Read our current pointer values.
            var drag = context.ReadValue<UnityInput.Gestures.PointerInput>();

            // Fix input for mice/pens
            if (device is Mouse)
            {
                drag.InputId = UnityEngine.EventSystems.PointerInputModule.kMouseLeftId;
            }
            else if (device is Pen)
            {
                drag.InputId = int.MinValue;
            }

            return drag;
        }

        protected void OnPointerActionStarted(InputAction.CallbackContext context)
        {
            var touch = retrievePointerInput(context);

            if (systemToInternalId.TryGetValue(touch.InputId, out var touchState) &&
                touchState.Phase != UnityEngine.TouchPhase.Canceled)
            {
                // Ending previous touch (missed a frame)
                internalRemovePointer(touchState.Pointer);
                systemToInternalId[touch.InputId] = new TouchState(internalAddPointer(touch.Position));
            }
            else
            {
                systemToInternalId.Add(touch.InputId, new TouchState(internalAddPointer(touch.Position)));
            }
        }

        protected void OnPointerAction(InputAction.CallbackContext context)
        {
            var touch = retrievePointerInput(context);

            if (touch.Contact)
            {
                if (systemToInternalId.TryGetValue(touch.InputId, out var touchState))
                {
                    if (touchState.Phase != UnityEngine.TouchPhase.Canceled)
                    {
                        touchState.Pointer.Position = remapCoordinates(touch.Position);
                        updatePointer(touchState.Pointer);
                    }
                }
                else
                {
                    // Missed began phase
                    systemToInternalId.Add(touch.InputId, new TouchState(internalAddPointer(touch.Position)));
                }
            }
            else
            {
                if (systemToInternalId.TryGetValue(touch.InputId, out var touchState))
                {
                    systemToInternalId.Remove(touch.InputId);
                    if (touchState.Phase != UnityEngine.TouchPhase.Canceled)
                        internalRemovePointer(touchState.Pointer);
                }
                else
                {
                    // Missed one finger begin-end transition
                    var pointer = internalAddPointer(touch.Position);
                    internalRemovePointer(pointer);
                }
            }
        }

        protected void OnPointerActionCanceled(InputAction.CallbackContext context)
        {
            var touch = retrievePointerInput(context);

            if (systemToInternalId.TryGetValue(touch.InputId, out var touchState))
            {
                systemToInternalId.Remove(touch.InputId);
                if (touchState.Phase != UnityEngine.TouchPhase.Canceled)
                    internalCancelPointer(touchState.Pointer);
            }
            else
            {
                // Missed one finger begin-end transition
                var pointer = internalAddPointer(touch.Position);
                internalCancelPointer(pointer);
            }
        }
#endif

        #region Public methods

        /// <inheritdoc />
        public bool UpdateInput()
        {
#if ENABLE_INPUT_SYSTEM
            return systemToInternalId.Count > 0;
#else
#if UNITY_5_6_OR_NEWER
            updateSampler.Begin();
#endif

            for (var i = 0; i < Input.touchCount; ++i)
            {
                var t = Input.GetTouch(i);

                TouchState touchState;
                switch (t.phase)
                {
                    case UnityEngine.TouchPhase.Began:
                        if (systemToInternalId.TryGetValue(t.fingerId, out touchState) && touchState.Phase != UnityEngine.TouchPhase.Canceled)
                        {
                            // Ending previous touch (missed a frame)
                            internalRemovePointer(touchState.Pointer);
                            systemToInternalId[t.fingerId] = new TouchState(internalAddPointer(t.position));
                        }
                        else
                        {
                            systemToInternalId.Add(t.fingerId, new TouchState(internalAddPointer(t.position)));
                        }
                        break;
                    case UnityEngine.TouchPhase.Moved:
                        if (systemToInternalId.TryGetValue(t.fingerId, out touchState))
                        {
                            if (touchState.Phase != UnityEngine.TouchPhase.Canceled)
                            {
                                touchState.Pointer.Position = remapCoordinates(t.position);
                                updatePointer(touchState.Pointer);
                            }
                        }
                        else
                        {
                            // Missed began phase
                            systemToInternalId.Add(t.fingerId, new TouchState(internalAddPointer(t.position)));
                        }
                        break;
                    // NOTE: Unity touch on Windows reports Cancelled as Ended
                    // when a touch goes out of display boundary
                    case UnityEngine.TouchPhase.Ended:
                        if (systemToInternalId.TryGetValue(t.fingerId, out touchState))
                        {
                            systemToInternalId.Remove(t.fingerId);
                            if (touchState.Phase != UnityEngine.TouchPhase.Canceled) internalRemovePointer(touchState.Pointer);
                        }
                        else
                        {
                            // Missed one finger begin-end transition
                            var pointer = internalAddPointer(t.position);
                            internalRemovePointer(pointer);
                        }
                        break;
                    case UnityEngine.TouchPhase.Canceled:
                        if (systemToInternalId.TryGetValue(t.fingerId, out touchState))
                        {
                            systemToInternalId.Remove(t.fingerId);
                            if (touchState.Phase != UnityEngine.TouchPhase.Canceled) internalCancelPointer(touchState.Pointer);
                        }
                        else
                        {
                            // Missed one finger begin-end transition
                            var pointer = internalAddPointer(t.position);
                            internalCancelPointer(pointer);
                        }
                        break;
                    case UnityEngine.TouchPhase.Stationary:
                        if (systemToInternalId.TryGetValue(t.fingerId, out touchState)) {}
                        else
                        {
                            // Missed begin phase
                            systemToInternalId.Add(t.fingerId, new TouchState(internalAddPointer(t.position)));
                        }
                        break;
                }
            }

#if UNITY_5_6_OR_NEWER
            updateSampler.End();
#endif

            return Input.touchCount > 0;
#endif
        }

        /// <inheritdoc />
        public void UpdateResolution(int width, int height) {}

        /// <inheritdoc />
        public bool CancelPointer(Pointers.Pointer pointer, bool shouldReturn)
        {
            var touch = pointer as TouchPointer;
            if (touch == null) return false;

            int fingerId = -1;
            foreach (var touchState in systemToInternalId)
            {
                if (touchState.Value.Pointer == touch && touchState.Value.Phase != UnityEngine.TouchPhase.Canceled)
                {
                    fingerId = touchState.Key;
                    break;
                }
            }
            if (fingerId > -1)
            {
                internalCancelPointer(touch);
                if (shouldReturn) systemToInternalId[fingerId] = new TouchState(internalReturnPointer(touch));
                else systemToInternalId[fingerId] = new TouchState(touch, UnityEngine.TouchPhase.Canceled);
                return true;
            }
            return false;
        }

        /// <inheritdoc />
        public bool DiscardPointer(Pointers.Pointer pointer)
        {
            var p = pointer as TouchPointer;
            if (p == null) return false;

            touchPool.Release(p);
            return true;
        }

        /// <summary>
        /// Releases resources.
        /// </summary>
        public void Dispose()
        {
            foreach (var touchState in systemToInternalId)
            {
                if (touchState.Value.Phase != UnityEngine.TouchPhase.Canceled) internalCancelPointer(touchState.Value.Pointer);
            }
            systemToInternalId.Clear();
        }

#endregion

#region Private functions

        private Pointers.Pointer internalAddPointer(Vector2 position)
        {
            pointersNum++;
            var pointer = touchPool.Get();
            pointer.Position = remapCoordinates(position);
            pointer.Buttons |= Pointers.Pointer.PointerButtonState.FirstButtonDown | Pointers.Pointer.PointerButtonState.FirstButtonPressed;
            addPointer(pointer);
            pressPointer(pointer);
            return pointer;
        }

        private TouchPointer internalReturnPointer(TouchPointer pointer)
        {
            pointersNum++;
            var newPointer = touchPool.Get();
            newPointer.CopyFrom(pointer);
            pointer.Buttons |= Pointers.Pointer.PointerButtonState.FirstButtonDown | Pointers.Pointer.PointerButtonState.FirstButtonPressed;
            newPointer.Flags |= Pointers.Pointer.FLAG_RETURNED;
            addPointer(newPointer);
            pressPointer(newPointer);
            return newPointer;
        }

        private void internalRemovePointer(Pointers.Pointer pointer)
        {
            pointersNum--;
            pointer.Buttons &= ~Pointers.Pointer.PointerButtonState.FirstButtonPressed;
            pointer.Buttons |= Pointers.Pointer.PointerButtonState.FirstButtonUp;
            releasePointer(pointer);
            removePointer(pointer);
        }

        private void internalCancelPointer(Pointers.Pointer pointer)
        {
            pointersNum--;
            cancelPointer(pointer);
        }

        private Vector2 remapCoordinates(Vector2 position)
        {
            if (CoordinatesRemapper != null) return CoordinatesRemapper.Remap(position);
            return position;
        }

        private void resetPointer(Pointers.Pointer p)
        {
            p.INTERNAL_Reset();
        }

        private TouchPointer newPointer()
        {
            return new TouchPointer(input);
        }

#endregion

        private struct TouchState
        {
            public Pointers.Pointer Pointer;
            public UnityEngine.TouchPhase Phase;

            public TouchState(Pointers.Pointer pointer, UnityEngine.TouchPhase phase = UnityEngine.TouchPhase.Began)
            {
                Pointer = pointer;
                Phase = phase;
            }
        }
    }
}