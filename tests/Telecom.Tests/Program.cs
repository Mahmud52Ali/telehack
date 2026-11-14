using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using Telecom.App;
using Telecom.Core;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private static string fixturesDirectory;
		private const string Ivanov = "Иванов Иван Иванович";
		private const string Petrov = "Петров Петр Петрович";

		// Запускает проверки под настоящим .NET Framework; ненулевой код означает сбой.
		private static int Main(string[] args)
		{
			Console.OutputEncoding = System.Text.Encoding.UTF8;
			try
			{
				fixturesDirectory = args.Length == 0 ? FindFixtures() : Path.GetFullPath(args[0]);
				var tests = new List<KeyValuePair<string, Action>>();
				tests.Add(new KeyValuePair<string, Action>("Целевая платформа всех сборок — .NET Framework 4.5", CheckTargetFramework));
				AddParserTests(tests);
				AddScannerTests(tests);
				AddLoggerTests(tests);
				AddConfigurationTests(tests);
				AddComputerTests(tests);
				AddDatabaseTests(tests);
				int failures = 0;
				foreach (KeyValuePair<string, Action> test in tests)
				{
					try
					{
						test.Value();
						Console.WriteLine("PASS: " + test.Key);
					}
					catch (Exception exception)
					{
						failures++;
						Console.Error.WriteLine("FAIL: " + test.Key + Environment.NewLine + exception);
					}
				}

				Console.WriteLine("Проверок: " + tests.Count + "; ошибок: " + failures);
				return failures == 0 ? 0 : 1;
			}
			catch (Exception exception)
			{
				Console.Error.WriteLine(exception);
				return 1;
			}
		}

		// Находит исходные публичные примеры без копирования сертификатов в проект тестов.
		private static string FindFixtures()
		{
			var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
			while (directory != null)
			{
				string candidate = Path.Combine(directory.FullName, "Test_sert");
				if (Directory.Exists(candidate))
				{
					return candidate;
				}

				directory = directory.Parent;
			}

			throw new DirectoryNotFoundException("Укажите путь к Test_sert первым аргументом.");
		}

		// Возвращает путь к неизменённому сертификату организаторов.
		private static string Fixture(string owner, string extension)
		{
			return Path.Combine(fixturesDirectory, owner, owner + extension);
		}

		// Проверяет метаданные платформы, а не только наличие установленного современного SDK.
		private static void CheckTargetFramework()
		{
			foreach (Assembly assembly in new[] { typeof(CertificateParser).Assembly, typeof(WindowsFileSystem).Assembly, Assembly.GetExecutingAssembly() })
			{
				var target = (TargetFrameworkAttribute)Attribute.GetCustomAttribute(assembly, typeof(TargetFrameworkAttribute));
				Check.Equal(".NETFramework,Version=v4.5", target.FrameworkName);
			}
		}
	}
}
