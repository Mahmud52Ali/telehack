using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Telecom.App.Forms;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private const BindingFlags PrivateMembers = BindingFlags.Instance | BindingFlags.NonPublic;

		// Читает закрытое поле элемента окна. Если у самого элемента такого поля нет, ищет его во вложенных
		// страницах и панелях (в порядке Controls), чтобы тесты не зависели от того, в какой класс вынесен элемент.
		private static T FormField<T>(Control control, string name)
		{
			object value;
			if (!TryFindField(control, name, out value))
			{
				throw new MissingFieldException(control.GetType().Name, name);
			}

			return (T)value;
		}

		// Вызывает закрытый метод элемента окна или вложенной в него страницы и возвращает результат.
		private static object InvokeMethod(Control control, string name, params object[] arguments)
		{
			Control owner = FindDeclaringControl(control, name);
			if (owner == null)
			{
				throw new MissingMethodException(control.GetType().Name, name);
			}

			return owner.GetType().GetMethod(name, PrivateMembers).Invoke(owner, arguments);
		}

		// Ищет поле сначала у самого элемента, затем во вложенных элементах.
		private static bool TryFindField(Control control, string name, out object value)
		{
			FieldInfo field = control.GetType().GetField(name, PrivateMembers);
			if (field != null)
			{
				value = field.GetValue(control);
				return true;
			}

			foreach (Control child in control.Controls)
			{
				if (TryFindField(child, name, out value))
				{
					return true;
				}
			}

			value = null;
			return false;
		}

		// Находит элемент, в классе которого объявлен метод с указанным именем.
		private static Control FindDeclaringControl(Control control, string name)
		{
			if (control.GetType().GetMethod(name, PrivateMembers) != null)
			{
				return control;
			}

			foreach (Control child in control.Controls)
			{
				Control owner = FindDeclaringControl(child, name);
				if (owner != null)
				{
					return owner;
				}
			}

			return null;
		}

		// Обрабатывает WinForms-продолжения асинхронных операций, пока окно не завершит текущую операцию.
		private static void WaitForFormIdle(MainForm form)
		{
			object operations = FormField<object>(form, "operations");
			PropertyInfo isBusy = operations.GetType().GetProperty("IsBusy", PrivateMembers);
			var timer = Stopwatch.StartNew();
			do
			{
				Application.DoEvents();
				if (!(bool)isBusy.GetValue(operations, null))
				{
					return;
				}

				Thread.Sleep(1);
			}
			while (timer.Elapsed < TimeSpan.FromSeconds(10));

			throw new TimeoutException("Окно не завершило операцию с тестовыми адаптерами.");
		}
	}
}
