using UnityEngine;
using UnityEngine.EventSystems;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentSavedExitPromptButtonClickTracer : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerClickHandler,
        ISelectHandler,
        ISubmitHandler
    {
        private string _label;
        private string _configuredPath;

        public static ExperimentSavedExitPromptButtonClickTracer Ensure(GameObject target, string label, string configuredPath)
        {
            if (target == null)
            {
                return null;
            }

            ExperimentSavedExitPromptButtonClickTracer tracer = target.GetComponent<ExperimentSavedExitPromptButtonClickTracer>();
            if (tracer == null)
            {
                tracer = target.AddComponent<ExperimentSavedExitPromptButtonClickTracer>();
            }

            tracer._label = label ?? target.name;
            tracer._configuredPath = string.IsNullOrWhiteSpace(configuredPath)
                ? GetTransformPath(target.transform)
                : configuredPath;

            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_button_click_tracer_attached | button={tracer._label} path={tracer._configuredPath}");
            return tracer;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            LogPointer("saved_exit_prompt_pointer_enter", eventData);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            LogPointer("saved_exit_prompt_pointer_exit", eventData);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            LogPointer("saved_exit_prompt_pointer_down", eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            LogPointer("saved_exit_prompt_pointer_up", eventData);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            LogPointer("saved_exit_prompt_button_clicked", eventData);
        }

        public void OnSelect(BaseEventData eventData)
        {
            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_select | button={_label} path={ResolvedPath()} frame={Time.frameCount} time={Time.time:F3}");
        }

        public void OnSubmit(BaseEventData eventData)
        {
            Debug.Log($"[P46D-PAUSE] saved_exit_prompt_submit | button={_label} path={ResolvedPath()} frame={Time.frameCount} time={Time.time:F3}");
        }

        private void LogPointer(string eventName, PointerEventData eventData)
        {
            Debug.Log(
                $"[P46D-PAUSE] {eventName} | button={_label} path={ResolvedPath()} " +
                $"pointerId={eventData?.pointerId.ToString() ?? "null"} button={eventData?.button.ToString() ?? "null"} " +
                $"position={eventData?.position.ToString() ?? "null"} frame={Time.frameCount} time={Time.time:F3}");
        }

        private string ResolvedPath()
        {
            return transform == null
                ? _configuredPath
                : GetTransformPath(transform);
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
            {
                return "<null>";
            }

            string path = transform.name;
            Transform current = transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }
    }
}
