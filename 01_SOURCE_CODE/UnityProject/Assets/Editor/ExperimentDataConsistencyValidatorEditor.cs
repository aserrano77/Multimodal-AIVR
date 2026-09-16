using Autonomy.Domain;
using UnityEditor;
using UnityEngine;

public static class ExperimentDataConsistencyValidatorEditor
{
    [MenuItem("Experiment/Validate Run Data Consistency...")]
    public static void ValidateRunFolder()
    {
        string folder = EditorUtility.OpenFolderPanel("Select TiagoExperimentTelemetry run folder", "Logs", string.Empty);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        string output = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Logs", "ExperimentDataValidation");
        ExperimentDataValidationReport report = ExperimentDataConsistencyValidator.ValidateRunDirectory(folder, output);
        Debug.Log(
            $"[ExperimentDataConsistencyValidator] {report.Status} run_id={report.RunId} events={report.EventCount} samples={report.SampleCount}\nMarkdown: {report.MarkdownPath}\nJSON: {report.JsonPath}");
        EditorUtility.RevealInFinder(report.MarkdownPath);
    }
}
