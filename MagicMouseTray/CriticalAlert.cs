// SPDX-License-Identifier: MIT
using System.Drawing;
using System.Windows.Forms;

namespace MagicMouseTray;

// Persistent always-on-top window shown at 0-1% battery.
// TrayApp decides when to close (AA death stays on disconnect; v3 USB-C closes).
internal sealed class CriticalAlert : Form
{
    internal CriticalAlert(string body)
    {
        Text = "Magic Tray — Battery Critical";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(340, 130);
        BackColor = Color.FromArgb(40, 0, 0);

        var icon = SystemIcons.Warning;
        var iconBox = new PictureBox
        {
            Image = icon.ToBitmap(),
            SizeMode = PictureBoxSizeMode.StretchImage,
            Size = new Size(32, 32),
            Location = new Point(16, 20),
            BackColor = Color.Transparent
        };

        var label = new Label
        {
            Text = body,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10f),
            AutoSize = false,
            Size = new Size(270, 48),
            Location = new Point(60, 16),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var dismiss = new Button
        {
            Text = "Dismiss",
            Size = new Size(90, 28),
            Location = new Point(125, 84),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(90, 30, 30)
        };
        dismiss.FlatAppearance.BorderColor = Color.FromArgb(160, 60, 60);
        dismiss.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { iconBox, label, dismiss });
    }
}
