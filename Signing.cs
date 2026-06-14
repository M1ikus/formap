using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace formap;

/// <summary>
/// Ed25519 sign-only support for v8 (Pillar 2 — provenance + integrity). ODbL-clean: a signature restricts
/// nothing, so it is NOT a TPM. No encryption, no anti-tamper on the OSM data itself.
///
/// Streaming-friendly design (see <see cref="BinaryFormatV8"/>): the writer stores a per-LOD SHA-256 of each
/// compressed block in the tile index, then Ed25519-signs the entire index byte region. A reader verifies the
/// (small) signed index once at open, then hashes each block on demand and compares to the trusted index hash —
/// full integrity without reading the whole multi-GB file up front.
///
/// Keys are raw 32-byte Ed25519 values: <c>path.priv</c> = 32-byte private seed, <c>path.pub</c> = 32-byte
/// public key. Signature = 64 bytes.
/// </summary>
public static class Signing
{
    public const int PrivateKeySize = 32;
    public const int PublicKeySize = 32;
    public const int SignatureSize = 64;

    /// <summary>Generates a fresh Ed25519 keypair and writes <paramref name="path"/>.priv (32-byte private seed)
    /// and <paramref name="path"/>.pub (32-byte public key).</summary>
    public static void GenerateKeypair(string path)
    {
        var random = new Org.BouncyCastle.Security.SecureRandom();
        var priv = new Ed25519PrivateKeyParameters(random);
        var pub = priv.GeneratePublicKey();

        File.WriteAllBytes(path + ".priv", priv.GetEncoded()); // 32-byte raw seed
        File.WriteAllBytes(path + ".pub", pub.GetEncoded());   // 32-byte raw public key
    }

    /// <summary>Ed25519 signature (64 bytes) of <paramref name="data"/> using a 32-byte raw private seed.</summary>
    public static byte[] Sign(byte[] privateKey32, byte[] data)
    {
        var priv = new Ed25519PrivateKeyParameters(privateKey32, 0);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, priv);
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    /// <summary>Verifies a 64-byte Ed25519 signature over <paramref name="data"/> against a 32-byte raw public key.</summary>
    public static bool Verify(byte[] publicKey32, byte[] data, byte[] sig64)
    {
        var pub = new Ed25519PublicKeyParameters(publicKey32, 0);
        var verifier = new Ed25519Signer();
        verifier.Init(forSigning: false, pub);
        verifier.BlockUpdate(data, 0, data.Length);
        return verifier.VerifySignature(sig64);
    }
}
