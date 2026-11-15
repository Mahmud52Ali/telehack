using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Telecom.Core.Certificates
{
	public sealed class CertificateParser
	{
		private const uint CertificatePropertyId = 32;
		private const string CryptoProLicenseOid = "1.2.643.2.2.49.2";

		// Читает открытый сертификат DER, Base64/PEM или сериализованный элемент хранилища Windows.
		public CertificateInfo Parse(byte[] fileContents, string sourcePath)
		{
			if (fileContents == null || fileContents.Length == 0)
			{
				throw new InvalidDataException("Файл сертификата пуст.");
			}

			byte[] certificateBytes = DecodeText(fileContents);
			X509ContentType contentType = GetContentType(certificateBytes);
			if (contentType == X509ContentType.SerializedCert || contentType == X509ContentType.Unknown)
			{
				certificateBytes = ReadSerializedCertificate(certificateBytes);
				contentType = GetContentType(certificateBytes);
			}

			// Проверяем тип до импорта: даже переименованный PFX не должен импортировать закрытый ключ.
			if (contentType != X509ContentType.Cert)
			{
				throw new InvalidDataException("Ожидался открытый сертификат. Контейнеры ключей и коллекции не поддерживаются.");
			}

			var certificate = new X509Certificate2(certificateBytes);
			try
			{
				bool hasEmbeddedLicense = false;
				foreach (X509Extension extension in certificate.Extensions)
				{
					if (extension.Oid.Value == CryptoProLicenseOid)
					{
						hasEmbeddedLicense = true;
						break;
					}
				}

				return new CertificateInfo(certificate.SerialNumber,
					certificate.GetNameInfo(X509NameType.SimpleName, false),
					certificate.GetNameInfo(X509NameType.SimpleName, true),
					sourcePath, hasEmbeddedLicense, certificate.NotAfter.ToUniversalTime());
			}
			finally
			{
				// В .NET Framework 4.5 X509Certificate2 ещё не реализует IDisposable.
				certificate.Reset();
			}
		}

		// Преобразует текстовый сертификат в DER, оставляя бинарные форматы без изменений.
		private static byte[] DecodeText(byte[] contents)
		{
			string text = Encoding.UTF8.GetString(contents).Trim().TrimStart('\uFEFF').Trim();
			const string begin = "-----BEGIN CERTIFICATE-----";
			const string end = "-----END CERTIFICATE-----";
			if (text.StartsWith(begin, StringComparison.Ordinal))
			{
				if (!text.EndsWith(end, StringComparison.Ordinal))
				{
					throw new InvalidDataException("Не найден конец PEM-сертификата.");
				}

				text = text.Substring(begin.Length, text.Length - begin.Length - end.Length);
				return Convert.FromBase64String(text);
			}

			foreach (char character in text)
			{
				if (!char.IsWhiteSpace(character) && !(character >= 'A' && character <= 'Z')
					&& !(character >= 'a' && character <= 'z') && !(character >= '0' && character <= '9')
					&& character != '+' && character != '/' && character != '=')
				{
					return contents;
				}
			}

			return Convert.FromBase64String(text);
		}

		// Определяет формат без импорта сертификатов или закрытых ключей в системное хранилище.
		private static X509ContentType GetContentType(byte[] contents)
		{
			if (contents.Length == 0)
			{
				return X509ContentType.Unknown;
			}

			try
			{
				return X509Certificate2.GetCertContentType(contents);
			}
			catch (CryptographicException)
			{
				// Для неизвестного формата далее выполняется проверка структуры сериализованного элемента.
				return X509ContentType.Unknown;
			}
		}

		// Извлекает свойство 32; границы проверяются до преобразования длины и выделения памяти.
		private static byte[] ReadSerializedCertificate(byte[] contents)
		{
			int offset = 0;
			while (contents.Length - offset >= 12)
			{
				uint propertyId = BitConverter.ToUInt32(contents, offset);
				uint propertyLength = BitConverter.ToUInt32(contents, offset + 8);
				offset += 12;
				if (propertyLength > (uint)(contents.Length - offset))
				{
					throw new InvalidDataException("Повреждённая длина свойства в файле хранилища.");
				}

				if (propertyId == CertificatePropertyId)
				{
					if (propertyLength == 0)
					{
						throw new InvalidDataException("Свойство сертификата в файле хранилища пусто.");
					}

					var certificateBytes = new byte[(int)propertyLength];
					Buffer.BlockCopy(contents, offset, certificateBytes, 0, certificateBytes.Length);
					return certificateBytes;
				}

				offset += (int)propertyLength;
			}

			throw new InvalidDataException("Файл не содержит открытого сертификата или свойства 32 хранилища Windows.");
		}
	}
}
