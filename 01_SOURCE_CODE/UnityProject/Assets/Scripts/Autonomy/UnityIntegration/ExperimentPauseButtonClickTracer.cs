using UnityEngine;
using UnityEngine.EventSystems;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentPauseButtonClickTracer : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerClickHandler,
        ISelectHandler,
        ISubmitHandler
    {
        private const string UiLogPrefix = "[P46D-PAUSE-UI]";
        private static float _lastPointerEventRealtime = -999f;

        [SerializeField] private string _label;
        [SerializeField] private string _configuredPath;

        public static ExperimentPauseButtonClickTracer Ensure(GameObject target, string label, string configuredPath)
        {
            if (target == null)
            {
                return null;
            }

            ExperimentPauseButtonClickTracer tracer = target.GetComponent<ExperimentPauseButtonClickTracer>();
            if (tracer == null)
            {
                tracer = target.AddComponent<ExperimentPauseButtonClickTracer>();
            }

            tracer._label = label ?? string.Empty;
            tracer._configuredPath = configuredPath ?? string.Empty;
            return tracer;
        }

        public static float SecondsSinceLastPointerEvent()
        {
            return Time.realtimeSinceStartup - _lastPointerEventRealtime;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            Log("pause_ui_pointer_enter", eventData);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            Log("pause_ui_pointer_exit", eventData);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            Log("pause_ui_pointer_down", eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            Log("pause_ui_pointer_up", eventData);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            Log("pause_ui_button_clicked", eventData);
        }

        public void OnSelect(BaseEventData eventData)
        {
            Log("pause_ui_button_selected", eventData);
        }

        public void OnSubmit(BaseEventData eventData)
        {
            Log("pause_ui_button_submitted", eventData);
        }

        private void Log(string eventName, BaseEventData eventData)
        {
            _lastPointerEventRealtime = Time.realtimeSinceStartup;
            PointerEventData pointer = eventData as PointerEventData;
            Debug.Log($"{UiLogPrefix} {eventName} | label=\"{_label}\" path={GetPath(transform)} configured_path={_configuredPath} activeSelf={gameObject.activeSelf} activeInHierarchy={gameObject.activeInHierarchy} pointerId={(pointer != null ? pointer.pointerId : 0)} button={(pointer != null ? pointer.button.ToString() : string.Empty)} position={(pointer != null ? pointer.position.ToString() : string.Empty)} eligibleForClick={(pointer != null && pointer.eligibleForClick)} clickCount={(pointer != null ? pointer.clickCount : 0)} eventSystem={(EventSystem.current != null ? EventSystem.current.name : string.Empty)} inputModule={(EventSystem.current != null && EventSystem.current.currentInputModule != null ? EventSystem.current.currentInputModule.GetType().FullName : string.Empty)} frame={Time.frameCount} time={Time.realtimeSinceStartup:0.000}");
        }

        private static string GetPath(Transform target)
        {
            if (target == null)
            {
                return string.Empty;
            }

            string path = target.name;
            Transform current = target.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }
    }
}
