using System;
using System.Drawing;
using System.Windows.Forms;

namespace Telecom.App.Theming
{
	internal sealed class AppearanceTabControl : TabControl
	{
		private bool dark;

		// Сохраняет штатную навигацию и переключает только отрисовку панели вкладок.
		internal void UseDarkColors(bool enabled)
		{
			dark = enabled;
			SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
				| ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, dark);
			UpdateStyles();
			Invalidate();
		}

		// Рисует подписи в прямоугольниках, рассчитанных штатным TabControl.
		protected override void OnPaint(PaintEventArgs e)
		{
			e.Graphics.Clear(ThemePalette.DarkSurface);
			using (var active = new SolidBrush(ThemePalette.DarkButton))
			using (var border = new Pen(ThemePalette.DarkBorder))
			{
				Rectangle frame = DisplayRectangle;
				frame.Inflate(1, 1);
				e.Graphics.DrawRectangle(border, frame);
				for (int index = 0; index < TabCount; index++)
				{
					Rectangle bounds = GetTabRect(index);
					if (!e.ClipRectangle.IntersectsWith(bounds))
					{
						continue;
					}

					TabPage page = TabPages[index];
					bool selected = SelectedIndex == index;
					if (selected)
					{
						e.Graphics.FillRectangle(active, bounds);
						e.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
					}

					Color text = page.Enabled ? ThemePalette.DarkText : ThemePalette.DarkMutedText;
					TextRenderer.DrawText(e.Graphics, page.Text, Font, bounds, text,
						TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
					if (selected && Focused && ShowFocusCues)
					{
						bounds.Inflate(-2, -2);
						ControlPaint.DrawFocusRectangle(e.Graphics, bounds, text, ThemePalette.DarkButton);
					}
				}
			}
		}

		// Подгоняет вкладки под шрифт. При увеличении использует ширину самой длинной подписи.
		protected override void OnFontChanged(EventArgs e)
		{
			base.OnFontChanged(e);
			int width = 0;
			if (Font.SizeInPoints > 9F)
			{
				foreach (TabPage page in TabPages)
				{
					width = Math.Max(width, TextRenderer.MeasureText(page.Text, Font).Width + 12);
				}
			}

			SizeMode = width > 0 ? TabSizeMode.Fixed : TabSizeMode.Normal;
			ItemSize = new Size(width, Font.Height + 4);
		}

		// Перерисовывает выделение при смене вкладки с мыши или клавиатуры.
		protected override void OnSelectedIndexChanged(EventArgs e)
		{
			base.OnSelectedIndexChanged(e);
			if (dark)
			{
				Invalidate();
			}
		}
	}
}
