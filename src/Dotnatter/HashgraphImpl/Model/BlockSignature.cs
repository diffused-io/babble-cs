﻿using Dotnatter.Util;

namespace Dotnatter.HashgraphImpl.Model
{
    public class BlockSignature
    {
        public byte[] Validator { get; set; }
        public int Index { get; set; }
        public string Signature { get; set; }

        public string ValidatorHex()
        {
            return Validator.ToHex();
        }

        public byte[] Marshal()
        {
            return this.SerializeToByteArray();
        }

        public static BlockBody Unmarshal(byte[] data)
        {
            return data.DeserializeFromByteArray<BlockBody>();
        }

        public WireBlockSignature ToWire()
        {
            return new WireBlockSignature
            {
                Index = Index,
                Signature = Signature
            };
        }
    }
}