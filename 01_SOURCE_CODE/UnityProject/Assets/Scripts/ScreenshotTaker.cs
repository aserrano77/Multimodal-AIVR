using UnityEngine;
using System;
using System.IO;

public class ScreenshotTaker : MonoBehaviour
{
    public string baseFileName = "captura";
    public int superSize = 2; // factor de escala (2 = 2x resolución nativa)

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F12))
        {
            // Carpeta de descargas del usuario (Windows, macOS, Linux)
            string downloadsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads"
            );

            // Crear timestamp para evitar sobrescrituras
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            // Nombre final del archivo
            string fileName = $"{baseFileName}_{timestamp}.png";

            // Ruta completa de guardado
            string path = Path.Combine(downloadsPath, fileName);

            // Captura de pantalla
            ScreenCapture.CaptureScreenshot(path, superSize);
            Debug.Log($"📸 Captura guardada en: {path}");
        }
    }
}
