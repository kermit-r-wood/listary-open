using System.Windows;
using System.Windows.Input;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.App;

/// <summary>
/// Detects active IME composition so Enter can be left for the input method
/// instead of confirming a search result.
/// </summary>
internal static class ImeInputGuard
{
    /// <summary>
    /// Returns true when Enter should not activate a result (IME is composing text).
    /// </summary>
    internal static bool ShouldDeferEnterToIme(DependencyObject? focusElement = null)
    {
        try
        {
            if (InputMethod.Current?.ImeState == InputMethodState.On && ImeComposition.IsComposing())
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // InputMethod.Current can throw when no dispatcher is active.
        }

        if (focusElement is not null
            && InputMethod.GetIsInputMethodEnabled(focusElement)
            && ImeComposition.IsComposing())
        {
            return true;
        }

        return ImeComposition.IsComposing();
    }
}
