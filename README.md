# Multimodal AI-VR: paquete privado de inspección

Este paquete permite al tutor consultar el código propio, la configuración esencial, el pipeline offline, resultados agregados y documentación técnica seleccionada.

Unity: 6000.0.68f1, revisión e1e9baaf294b.

Escenas principales:
- Assets/Scenes/experiment_start_scene.unity
- Assets/Scenes/final_scene.unity

Organización:
- 01_SOURCE_CODE: fuentes y recursos propios seleccionados.
- 02_UNITY_CONFIGURATION: configuración y manifiestos.
- 03_ANALYSIS_PIPELINE: pipeline genérico y pruebas.
- 04_AGGREGATED_RESULTS: tablas de grupo o condición.
- 05_DOCUMENTATION: documentación técnica segura.
- 06_ARTIFACT_REFERENCE: referencia verificable del binario congelado.

El paquete es privado y sirve para inspección. No garantiza una reconstrucción visual completa: omite datos individuales, binarios y recursos de terceros pendientes de licencia. MANIFEST_SHA256.csv enumera todos los archivos salvo el propio manifiesto, para evitar autorreferencia.