using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// A picker that had the keyboard's focus ring when its list opened has it again when the list closes. Fluent's picker takes the
/// focus back without saying that a key moved it, so the ring would go and a person who uses the keyboard could not see the focus.
/// </summary>
internal static class PickerFocus
{
    private static readonly ConditionalWeakTable<ComboBox, object> Ringed = [];

    private static bool _kept;

    /// <summary>Starts the rule for every picker in the process, once.</summary>
    public static void Keep()
    {
        if (_kept)
        {
            return;
        }

        _kept = true;
        ComboBox.IsDropDownOpenProperty.Changed.AddClassHandler<ComboBox>((picker, change) =>
        {
            if (change.NewValue is true)
            {
                if (picker.Classes.Contains(":focus-visible"))
                {
                    Ringed.AddOrUpdate(picker, picker);
                }
            }
            else if (Ringed.Remove(picker))
            {
                picker.Focus(NavigationMethod.Directional);
            }
        });
    }
}
