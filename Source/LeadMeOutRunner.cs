using UnityEngine;
using UnityEngine.InputSystem;

namespace LeadMeOut
{
    public class LeadMeOutRunner : MonoBehaviour
    {
        private void Awake()
        {
            Plugin.Logger.LogInfo("LeadMeOut: Runner Awake.");
        }

        private void Start()
        {
            Plugin.Logger.LogInfo("LeadMeOut: Runner Start.");
        }

        private void Update()
        {
            if (Plugin.InputActions != null && Plugin.InputActions.ToggleKey.WasPressedThisFrame())
            {
                Plugin.Logger.LogInfo("LeadMeOut: Toggle via InputUtils.");
                Plugin.ExitFinderInstance?.Toggle();
            }
            else if (Keyboard.current != null && Keyboard.current.lKey.wasPressedThisFrame)
            {
                Plugin.Logger.LogInfo("LeadMeOut: Toggle via raw keyboard.");
                Plugin.ExitFinderInstance?.Toggle();
            }

            Plugin.ExitFinderInstance?.Tick(Time.deltaTime);
        }
    }
}