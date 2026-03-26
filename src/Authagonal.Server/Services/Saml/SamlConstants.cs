namespace Authagonal.Server.Services.Saml;

public static class SamlConstants
{
    // XML Namespaces
    public const string Saml2Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
    public const string Saml2Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";
    public const string XmlDSig = "http://www.w3.org/2000/09/xmldsig#";

    // Bindings
    public const string HttpRedirectBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect";
    public const string HttpPostBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";

    // NameID Formats
    public const string NameIdEmail = "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress";
    public const string NameIdPersistent = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent";
    public const string NameIdTransient = "urn:oasis:names:tc:SAML:2.0:nameid-format:transient";
    public const string NameIdUnspecified = "urn:oasis:names:tc:SAML:2.0:nameid-format:unspecified";

    // Status
    public const string StatusSuccess = "urn:oasis:names:tc:SAML:2.0:status:Success";

    // Subject Confirmation
    public const string BearerConfirmation = "urn:oasis:names:tc:SAML:2.0:cm:bearer";
}
