// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using X509IssuerSerial = System.Security.Cryptography.Xml.X509IssuerSerial;

namespace Internal.Cryptography
{
    internal static class Helpers
    {
        public static byte[] CloneByteArray(this byte[] a)
        {
            return (byte[])(a.Clone());
        }

        /// <summary>
        /// This is not just a convenience wrapper for Array.Resize(). In DEBUG builds, it forces the array to move in memory even if no resize is needed. This should be used by
        /// helper methods that do anything of the form "call a native api once to get the estimated size, call it again to get the data and return the data in a byte[] array."
        /// Sometimes, that data consist of a native data structure containing pointers to other parts of the block. Using such a helper to retrieve such a block results in an intermittent
        /// AV. By using this helper, you make that AV repro every time.
        /// </summary>
        public static byte[] Resize(this byte[] a, int size)
        {
            Array.Resize(ref a, size);
#if DEBUG
            a = a.CloneByteArray();
#endif
            return a;
        }

        public static CmsRecipientCollection DeepCopy(this CmsRecipientCollection recipients)
        {
            CmsRecipientCollection recipientsCopy = new CmsRecipientCollection();
            foreach (CmsRecipient recipient in recipients)
            {
                X509Certificate2 originalCert = recipient.Certificate;
                X509Certificate2 certCopy = new X509Certificate2(originalCert.Handle);
                CmsRecipient recipientCopy = new CmsRecipient(recipient.RecipientIdentifierType, certCopy);
                recipientsCopy.Add(recipientCopy);
                GC.KeepAlive(originalCert);
            }
            return recipientsCopy;
        }

        public static byte[] UnicodeToOctetString(this string s)
        {
            byte[] octets = new byte[2 * (s.Length + 1)];
            Encoding.Unicode.GetBytes(s, 0, s.Length, octets, 0);
            return octets;
        }

        public static string OctetStringToUnicode(this byte[] octets)
        {
            if (octets.Length < 2)
                return string.Empty;   // Desktop compat: 0-length byte array maps to string.empty. 1-length byte array gets passed to Marshal.PtrToStringUni() with who knows what outcome.

            string s = Encoding.Unicode.GetString(octets, 0, octets.Length - 2);
            return s;
        }

        public static X509Certificate2Collection GetStoreCertificates(StoreName storeName, StoreLocation storeLocation, bool openExistingOnly)
        {
            using (X509Store store = new X509Store())
            {
                OpenFlags flags = OpenFlags.ReadOnly | OpenFlags.IncludeArchived;
                if (openExistingOnly)
                    flags |= OpenFlags.OpenExistingOnly;

                store.Open(flags);
                X509Certificate2Collection certificates = store.Certificates;
                return certificates;
            }
        }

        /// <summary>
        /// Note: Due to its use of X509Certificate2Collection.Find(), this returns a cloned certificate. So the caller should dispose it.
        /// </summary>
        public static X509Certificate2 TryFindMatchingCertificate(this X509Certificate2Collection certs, SubjectIdentifier recipientIdentifier)
        {
            SubjectIdentifierType recipientIdentifierType = recipientIdentifier.Type;
            switch (recipientIdentifierType)
            {
                case SubjectIdentifierType.IssuerAndSerialNumber:
                    {
                        X509IssuerSerial issuerSerial = (X509IssuerSerial)(recipientIdentifier.Value);

                        using (ClonedCertificates matches1 = certs.FindCertificates(X509FindType.FindBySerialNumber, issuerSerial.SerialNumber))
                        {
                            using (ClonedCertificates matches2 = matches1.FindCertificates(X509FindType.FindByIssuerDistinguishedName, issuerSerial.IssuerName))
                            {
                                // Desktop compat: We do not complain about multiple matches. Just take the first one and ignore the rest.
                                return matches2.Any ? matches2.ClaimResult() : null;
                            }
                        }
                    }

                case SubjectIdentifierType.SubjectKeyIdentifier:
                    {
                        string ski = (string)(recipientIdentifier.Value);
                        using (ClonedCertificates matches = certs.FindCertificates(X509FindType.FindBySubjectKeyIdentifier, ski))
                        {
                            // Desktop compat: We do not complain about multiple matches. Just take the first one and ignore the rest.
                            return matches.Any ? matches.ClaimResult() : null;
                        }
                    }

                default:
                    // RecipientInfo's can only be created by this package so if this an invalid type, it's the package's fault.
                    Debug.Fail($"Invalid recipientIdentifier type: {recipientIdentifierType}");
                    throw new CryptographicException();
            }
        }

        /// <summary>
        /// Useful helper for "upgrading" well-known CMS attributes to type-specific objects such as Pkcs9DocumentName, Pkcs9DocumentDescription, etc. 
        /// </summary>
        public static Pkcs9AttributeObject CreateBestPkcs9AttributeObjectAvailable(Oid oid, byte[] encodedAttribute)
        {
            Pkcs9AttributeObject attributeObject = new Pkcs9AttributeObject(oid, encodedAttribute);
            switch (oid.Value)
            {
                case Oids.DocumentName:
                    attributeObject = Upgrade<Pkcs9DocumentName>(attributeObject);
                    break;

                case Oids.DocumentDescription:
                    attributeObject = Upgrade<Pkcs9DocumentDescription>(attributeObject);
                    break;

                case Oids.SigningTime:
                    attributeObject = Upgrade<Pkcs9SigningTime>(attributeObject);
                    break;

                case Oids.ContentType:
                    attributeObject = Upgrade<Pkcs9ContentType>(attributeObject);
                    break;

                case Oids.MessageDigest:
                    attributeObject = Upgrade<Pkcs9MessageDigest>(attributeObject);
                    break;

                default:
                    break;
            }
            return attributeObject;
        }

        private static T Upgrade<T>(Pkcs9AttributeObject basicAttribute) where T : Pkcs9AttributeObject, new()
        {
            T enhancedAttribute = new T();
            enhancedAttribute.CopyFrom(basicAttribute);
            return enhancedAttribute;
        }
    }
}