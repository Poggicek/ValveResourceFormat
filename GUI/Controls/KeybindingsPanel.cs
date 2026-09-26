using System.Drawing;
using System.Windows.Forms;
using GUI.Utils;

namespace GUI.Controls
{
    /// <summary>
    /// A panel that displays multiple keybinding controls in a horizontal flow layout.
    /// </summary>
    public class KeybindingsPanel : FlowLayoutPanel
    {
        public KeybindingsPanel()
        {
            FlowDirection = FlowDirection.LeftToRight;
            WrapContents = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.Transparent;
            Padding = new Padding(this.AdjustForDPI(4), this.AdjustForDPI(4), this.AdjustForDPI(4), this.AdjustForDPI(4));
        }

        /// <summary>
        /// Updates the panel to display the specified keybindings.
        /// </summary>
        /// <param name="keybindings">List of keybindings to display</param>
        /// <returns>Whether the keycaps had to be created again, rather than only having their highlight changed.</returns>
        public bool SetKeybindings(List<KeybindingInfo> keybindings)
        {
            // Recreating the keycaps flickers, which shows when a viewer marks a different key on every key press
            if (Controls.Count == keybindings.Count)
            {
                var same = true;

                for (var i = 0; i < keybindings.Count && same; i++)
                {
                    same = Controls[i] is KeycapControl keycap
                        && keycap.KeyText == keybindings[i].KeyCombination
                        && keycap.Description == keybindings[i].Description;
                }

                if (same)
                {
                    for (var i = 0; i < keybindings.Count; i++)
                    {
                        ((KeycapControl)Controls[i]).Highlighted = keybindings[i].Highlighted;
                    }

                    return false;
                }
            }

            SuspendLayout();

            Controls.Clear();

            foreach (var binding in keybindings)
            {
                var keycap = new KeycapControl
                {
                    KeyText = binding.KeyCombination,
                    Description = binding.Description,
                    Highlighted = binding.Highlighted,
                    Margin = new Padding(0, 0, this.AdjustForDPI(4), 0)
                };
                Controls.Add(keycap);
            }

            ResumeLayout();

            return true;
        }
    }
}
