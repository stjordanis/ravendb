﻿// -----------------------------------------------------------------------
//  <copyright file="DefaultEncryptor.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
namespace Raven.Abstractions.Util.Encryptors
{
	using System.Security.Cryptography;

	public class DefaultEncryptor : IEncryptor
	{
		public DefaultEncryptor()
		{
			Hash = new DefaultHashEncryptor();
			Symmetrical = new DefaultSymmetricalEncryptor();
		}

		public IHashEncryptor Hash { get; private set; }

		public ISymmetricalEncryptor Symmetrical { get; private set; }

		private class DefaultHashEncryptor : HashEncryptorBase, IHashEncryptor
		{
			public int StorageHashSize
			{
				get
				{
					return 32;
				}
			}

			public byte[] ComputeForStorage(byte[] bytes)
			{
				return ComputeHash(SHA256.Create(), bytes);
			}

			public byte[] Compute(byte[] bytes)
			{
				return ComputeHash(MD5.Create(), bytes);
			}
		}

		private class DefaultSymmetricalEncryptor : ISymmetricalEncryptor
		{
			public byte[] Encrypt()
			{
				throw new System.NotImplementedException();
			}

			public byte[] Decrypt()
			{
				throw new System.NotImplementedException();
			}
		}
	}
}