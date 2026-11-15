using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Telecom.Core.Certificates;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		// Регистрирует форматы и повреждения, существенные для сертификатов и файлов Windows-хранилища.
		private static void AddParserTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			tests.Add(new KeyValuePair<string, Action>("DER: Иванов и Петров", CheckDerCertificates));
			tests.Add(new KeyValuePair<string, Action>("Base64 и PEM: Иванов и Петров", CheckTextCertificates));
			AddWindowsOnly(tests, "Сериализованные сертификаты Windows", CheckSerializedCertificates);
			AddWindowsOnly(tests, "Пропуск неизвестного свойства перед сертификатом", CheckUnknownSerializedProperty);
			tests.Add(new KeyValuePair<string, Action>("Отказ на пустом, обрезанном и постороннем содержимом", CheckInvalidCertificates));
			tests.Add(new KeyValuePair<string, Action>("Отказ на переполненной и нулевой длине свойства", CheckInvalidSerializedLengths));
			tests.Add(new KeyValuePair<string, Action>("PKCS#12 не импортируется как открытый сертификат", CheckPrivateKeyContainerRejected));
		}

		// Сверяет реальные сертификаты с заранее известными владельцами, УЦ и серийными номерами.
		private static void CheckDerCertificates()
		{
			AssertCertificate(File.ReadAllBytes(Fixture(Ivanov, ".cer")), Ivanov, "03A14016");
			AssertCertificate(File.ReadAllBytes(Fixture(Petrov, ".cer")), Petrov, "378F0E85");
		}

		// Проверяет текстовое представление обоих образцов с заголовком PEM и без него.
		private static void CheckTextCertificates()
		{
			foreach (string owner in new[] { Ivanov, Petrov })
			{
				string serial = owner == Ivanov ? "03A14016" : "378F0E85";
				string base64 = Convert.ToBase64String(File.ReadAllBytes(Fixture(owner, ".cer")), Base64FormattingOptions.InsertLineBreaks);
				AssertCertificate(Encoding.UTF8.GetBytes(base64), owner, serial);
				AssertCertificate(Encoding.UTF8.GetBytes("\uFEFF-----BEGIN CERTIFICATE-----\r\n" + base64 + "\r\n-----END CERTIFICATE-----\r\n"), owner, serial);
			}
		}

		// Экспортирует только публичный сертификат в формат Windows, не устанавливая его в систему.
		private static void CheckSerializedCertificates()
		{
			foreach (string owner in new[] { Ivanov, Petrov })
			{
				AssertCertificate(SerializeCertificateFixture(owner), owner, owner == Ivanov ? "03A14016" : "378F0E85");
			}
		}

		// Получает сериализованную запись из открытого образца без установки в хранилище.
		private static byte[] SerializeCertificateFixture(string owner)
		{
			var certificate = new X509Certificate2(Fixture(owner, ".cer"));
			try
			{
				return certificate.Export(X509ContentType.SerializedCert);
			}
			finally
			{
				certificate.Reset();
			}
		}

		// Проверяет поиск свойства 32 после другого свойства переменной длины.
		private static void CheckUnknownSerializedProperty()
		{
			using (var stream = new MemoryStream())
			using (var writer = new BinaryWriter(stream))
			{
				WriteProperty(writer, 11, new byte[] { 1, 2, 3 });
				WriteProperty(writer, 32, File.ReadAllBytes(Fixture(Ivanov, ".cer")));
				AssertCertificate(stream.ToArray(), Ivanov, "03A14016");
			}
		}

		// Проверяет, что испорченные входные данные не дают ложный сертификат.
		private static void CheckInvalidCertificates()
		{
			var parser = new CertificateParser();
			Check.Throws<InvalidDataException>(() => parser.Parse(new byte[0], "empty.cer"));
			Check.Throws<InvalidDataException>(() => parser.Parse(new byte[] { 0x30, 0x82, 0x03 }, "truncated.cer"));
			Check.Throws<InvalidDataException>(() => parser.Parse(Encoding.UTF8.GetBytes("это не сертификат"), "bad.cer"));
			Check.Throws<InvalidDataException>(() => parser.Parse(Encoding.UTF8.GetBytes("-----BEGIN CERTIFICATE-----"), "bad.pem"));
		}

		// Проверяет вредоносную длину до выделения массива и пустое свойство сертификата.
		private static void CheckInvalidSerializedLengths()
		{
			var parser = new CertificateParser();
			var oversized = new byte[12];
			Buffer.BlockCopy(BitConverter.GetBytes((uint)32), 0, oversized, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(uint.MaxValue), 0, oversized, 8, 4);
			Check.Throws<InvalidDataException>(() => parser.Parse(oversized, "oversized"));
			Buffer.BlockCopy(BitConverter.GetBytes((uint)0), 0, oversized, 8, 4);
			Check.Throws<InvalidDataException>(() => parser.Parse(oversized, "empty-property"));
		}

		// Проверяет отказ даже при маскировке P12 под CER или свойство сериализованного сертификата.
		private static void CheckPrivateKeyContainerRejected()
		{
			var parser = new CertificateParser();
			byte[] container = File.ReadAllBytes(Fixture(Ivanov, ".p12"));
			Check.Throws<InvalidDataException>(() => parser.Parse(container, "renamed.cer"));
			using (var stream = new MemoryStream())
			using (var writer = new BinaryWriter(stream))
			{
				WriteProperty(writer, 32, container);
				Check.Throws<InvalidDataException>(() => parser.Parse(stream.ToArray(), "wrapped-container"));
			}
		}

		// Проверяет поля по данным организаторов без сравнения с другим вызовом нашего парсера.
		private static void AssertCertificate(byte[] bytes, string owner, string serial)
		{
			CertificateInfo certificate = new CertificateParser().Parse(bytes, "source.cer");
			Check.Equal(owner, certificate.OwnerName);
			Check.Equal(owner, certificate.CertificationAuthority);
			Check.Equal(serial, certificate.SerialNumber);
			Check.Equal("source.cer", certificate.SourcePath);
			Check.Equal(false, certificate.IsCryptoProLicenseEmbedded);
			var expiresAt = owner == Ivanov
				? new DateTime(2027, 10, 26, 10, 21, 31, DateTimeKind.Utc)
				: new DateTime(2027, 10, 26, 10, 25, 28, DateTimeKind.Utc);
			Check.Equal(expiresAt, certificate.ExpiresAtUtc);
			Check.Equal(DateTimeKind.Utc, certificate.ExpiresAtUtc.Value.Kind);
		}

		// Формирует тестовый элемент сериализованного хранилища без создания системного хранилища.
		private static void WriteProperty(BinaryWriter writer, uint propertyId, byte[] bytes)
		{
			writer.Write(propertyId);
			writer.Write((uint)1);
			writer.Write((uint)bytes.Length);
			writer.Write(bytes);
		}
	}
}
