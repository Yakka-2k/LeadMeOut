using LethalCompanyInputUtils.Api;
using UnityEngine.InputSystem;

namespace LeadMeOut
{
    public class LeadMeOutInputActions : LcInputActions
    {
        [InputAction("<Keyboard>/l", Name = "Toggle Exit Markers")]
        public InputAction ToggleKey { get; set; }
    }
}
