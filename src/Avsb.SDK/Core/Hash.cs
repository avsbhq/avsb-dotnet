namespace Avsb.SDK.Core;

/// <summary>
/// MurmurHash3_x86_32: deterministic 32-bit hash for visitor bucketing.
/// Exact port of hash.ts. Uses uint (System.UInt32) so unsigned bit
/// semantics match the TypeScript >>> 0 promotion naturally.
/// </summary>
public static class Hash
{
    private const uint C1 = 0xcc9e2d51u;
    private const uint C2 = 0x1b873593u;

    public static uint Murmurhash3(string key, uint seed = 0)
    {
        uint h1 = seed;
        int len = key.Length;
        int nblocks = len >> 2;

        // Body — process 4-byte blocks.
        for (int i = 0; i < nblocks; i++)
        {
            int i4 = i * 4;
            uint k1 = (uint)(key[i4] & 0xff)
                | ((uint)(key[i4 + 1] & 0xff) << 8)
                | ((uint)(key[i4 + 2] & 0xff) << 16)
                | ((uint)(key[i4 + 3] & 0xff) << 24);

            k1 *= C1;
            k1 = RotateLeft(k1, 15);
            k1 *= C2;

            h1 ^= k1;
            h1 = RotateLeft(h1, 13);
            h1 = h1 * 5 + 0xe6546b64u;
        }

        // Tail — remaining bytes.
        int tail = nblocks * 4;
        uint k1Tail = 0;

        switch (len & 3)
        {
            case 3:
                k1Tail ^= (uint)(key[tail + 2] & 0xff) << 16;
                goto case 2;
            case 2:
                k1Tail ^= (uint)(key[tail + 1] & 0xff) << 8;
                goto case 1;
            case 1:
                k1Tail ^= (uint)(key[tail] & 0xff);
                k1Tail *= C1;
                k1Tail = RotateLeft(k1Tail, 15);
                k1Tail *= C2;
                h1 ^= k1Tail;
                break;
        }

        // Finalization mix — force all bits to avalanche.
        h1 ^= (uint)len;
        h1 ^= h1 >> 16;
        h1 *= 0x85ebca6bu;
        h1 ^= h1 >> 13;
        h1 *= 0xc2b2ae35u;
        h1 ^= h1 >> 16;

        return h1;
    }

    private static uint RotateLeft(uint value, int bits) =>
        (value << bits) | (value >> (32 - bits));
}
