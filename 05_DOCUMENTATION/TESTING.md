# Advertencias de ejecución de tests

## Unity Robotics URDF Importer — PlayMode

Antes de ejecutar `Unity.Robotics.UrdfImporter.Runtime.Tests` en PlayMode, haz una copia de seguridad de `Assets/Tests` o comprueba `cm status` inmediatamente después de la ejecución.

El fixture de terceros `RuntimeUrdfTests` contiene un defecto que puede eliminar `Assets/Tests` si la carpeta ya existía al arrancar la suite: su `SetUp` crea `Tests 1` cuando `Tests` ya existe, pero su `TearDown` borra `Tests` de todos modos. No debe confundirse la carpeta duplicada con un reemplazo válido de los tests del proyecto.
