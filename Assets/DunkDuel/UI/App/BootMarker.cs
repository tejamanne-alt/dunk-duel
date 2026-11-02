using UnityEngine;

namespace DunkDuel.UI
{
    /// <summary>
    /// The only object in the Boot scene (created by ProjectSetup through EditorSceneManager). Everything else
    /// is built in code by <see cref="AppBootstrap"/> after the scene loads.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BootMarker : MonoBehaviour
    {
    }
}
