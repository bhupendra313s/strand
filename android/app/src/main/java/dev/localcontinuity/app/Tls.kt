package dev.localcontinuity.app

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.net.Socket
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.Principal
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.Date
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.X509TrustManager
import javax.security.auth.x500.X500Principal

/**
 * Accepts exactly one server certificate: the hub cert whose SHA-256 was scanned from the QR code.
 * No CA, hostname, or expiry checks apply. The pin itself is the trust anchor.
 */
class PinnedTrustManager(private val pinnedSha256Hex: String) : X509TrustManager {
    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("No certificate")
        val actual = sha256Hex(leaf.encoded)
        if (!MessageDigest.isEqual(actual.toByteArray(), pinnedSha256Hex.toByteArray()))
            throw CertificateException("Hub certificate does not match the paired fingerprint")
    }
    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
        throw CertificateException("Not a server")
    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    fun sslContext(): SSLContext = SSLContext.getInstance("TLS").apply { init(null, arrayOf(this@PinnedTrustManager), SecureRandom()) }
}

/**
 * The phone's own TLS identity for the data-plane (screenshot) server. The EC P-256 key lives in
 * AndroidKeyStore and never leaves secure hardware. Its fingerprint goes to the hub in `hello`.
 */
object DeviceIdentity {
    private const val ALIAS = "continuity-fetch-tls"

    private val keyStore: KeyStore by lazy { KeyStore.getInstance("AndroidKeyStore").apply { load(null) } }

    @Synchronized
    fun certificate(): X509Certificate {
        if (!keyStore.containsAlias(ALIAS)) generate()
        return keyStore.getCertificate(ALIAS) as X509Certificate
    }

    fun fingerprint(): String = sha256Hex(certificate().encoded)

    private fun generate() {
        val now = System.currentTimeMillis()
        val spec = KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA384, KeyProperties.DIGEST_SHA512)
            .setCertificateSubject(X500Principal("CN=Continuity Android"))
            .setCertificateSerialNumber(BigInteger(64, SecureRandom()).abs().add(BigInteger.ONE))
            .setCertificateNotBefore(Date(now - 86_400_000L))
            .setCertificateNotAfter(Date(now + 20L * 365 * 86_400_000L))
            .build()
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply { initialize(spec) }.generateKeyPair()
    }

    fun serverSslContext(): SSLContext {
        val cert = certificate()
        val key = keyStore.getKey(ALIAS, null) as PrivateKey
        val km = object : X509ExtendedKeyManager() {
            override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?) =
                if (keyType == null || keyType.startsWith("EC")) ALIAS else null
            override fun chooseEngineServerAlias(keyType: String?, issuers: Array<out Principal>?, engine: SSLEngine?) =
                chooseServerAlias(keyType, issuers, null)
            override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?) = arrayOf(ALIAS)
            override fun getCertificateChain(alias: String?) = arrayOf(cert)
            override fun getPrivateKey(alias: String?) = key
            override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? = null
            override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?): String? = null
        }
        return SSLContext.getInstance("TLS").apply { init(arrayOf(km), null, SecureRandom()) }
    }
}
