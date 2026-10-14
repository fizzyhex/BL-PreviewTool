using UnityEngine;

namespace BLPTool
{
    /// <summary>
    /// Placeholder left by the realiser where a bundle component's script had no ExtSDK match,
    /// so its data couldn't be read. Safe to delete.
    /// </summary>
    [AddComponentMenu("")]
    public class BLPMissingScript : MonoBehaviour
    {
        [Tooltip("Hierarchy path of the GameObject in the source bundle")]
        public string SourcePath;
    }
}
