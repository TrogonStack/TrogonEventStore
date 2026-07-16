---
title: "Security"
---

## Security

For production use, it is important to configure TrogonEventStore security features to prevent unauthorised access
to your data.

Security features of TrogonEventStore include:

- [User management](#authentication) for allowing users with different roles to access the database
- [Access Control Lists](#access-control-lists) to restrict access to specific event streams
- Encryption in-flight using HTTPS and TLS

### Protocol security

TrogonEventStore supports gRPC for client communication and internal TCP for
cluster replication. It also has HTTP endpoints for the Admin UI, health,
metrics, and supported operator workflows.
TrogonEventStore also uses HTTP for the gossip seed endpoint, both internally for the cluster gossip, and
internally for clients that connect to the cluster using discovery mode.

All those protocols support encryption with TLS and SSL. Each protocol has its own security configuration, but
you can only use one set of certificates for both TLS and HTTPS.

The protocol security configuration depends a lot on the deployment topology and platform. We have created an
interactive [configuration tool](installation.md), which also has instructions on how to generate and install
the certificates and configure TrogonEventStore nodes to use them.

## Security options

Below you can find more details about each of the available security options.

### Running without security

Unlike previous versions, TrogonEventStore v20+ is secure by default. It means that you have to supply valid certificates and configuration for the database node to work.

We realise that many users want to try out the latest version with their existing applications, and also run a previous version of TrogonEventStore without any security in their internal networks.

For this to work, you can use the `Insecure` option:

| Format               | Syntax                |
|:---------------------|:----------------------|
| Command line         | `--insecure`          |
| YAML                 | `Insecure`            |
| Environment variable | `EVENTSTORE_INSECURE` |

**Default**: `false`

::: warning
When running with protocol security disabled, everything is sent unencrypted over the wire. In the previous version it included the server credentials. Sending username and password over the wire without encryption is not secure by definition, but it might give a false sense of security. To make things explicit, TrogonEventStore v20+ **does not use any authentication and authorisation** (including ACLs) when running insecure.
:::

### Disable TLS

Use `DisableTls` when authentication and authorization must remain enabled while transport encryption is
disabled.

| Format               | Syntax                   |
|:---------------------|:-------------------------|
| Command line         | `--disable-tls`          |
| YAML                 | `DisableTls`             |
| Environment variable | `EVENTSTORE_DISABLE_TLS` |

**Default**: `false`

::: warning
`DisableTls` sends credentials and all other traffic without encryption. Use it only on a trusted network or
when another network layer provides transport security.
:::

### Set initial passwords

We are adding an ability to set default admin and ops passwords on the first run of the database. It will not impact the existing credentials, the user can log into their accounts with existing passwords.

For this to work, you can use the `DefaultAdminPassword` option:

| Format               | Syntax                              |
|:---------------------|:------------------------------------|
| Environment variable | `EVENTSTORE_DEFAULT_ADMIN_PASSWORD` |

**Default**: `changeit`

For this to work, you can use the `DefaultOpsPassword` option:

| Format               | Syntax                              |
|:---------------------|:------------------------------------|
| Environment variable | `EVENTSTORE_DEFAULT_OPS_PASSWORD`   |

**Default**: `changeit`

::: warning
Due to security reasons the `DefaultAdminPassword` and `DefaultOpsPassword` options can only be set through environment variables. The user will receive the error message if they try to pass the options using command line or config file.
:::

### Anonymous access to streams

Historically, anonymous users with network access have been allowed to read/write streams that do not have access control lists.

This is now disabled by default but can be enabled by setting `AllowAnonymousStreamAccess` to `true`.

| Format               | Syntax                                     |
|:---------------------|:-------------------------------------------|
| Command line         | `--allow-anonymous-stream-access`          |
| YAML                 | `AllowAnonymousStreamAccess`               |
| Environment variable | `EVENTSTORE_ALLOW_ANONYMOUS_STREAM_ACCESS` |

**Default**: `false`

### Anonymous access to endpoints

Similarly to streams above, anonymous access has historically been available to some HTTP endpoints and node
diagnostic operations.

Anonymous access to the `HTTP OPTIONS` method and the client gossip read operation can now be configured with the following two options. By default the client gossip read operation is still accessible anonymously but `HTTP OPTIONS` is not.

The `AllowAnonymousEndpointAccess` option controls anonymous access to these endpoints. Setting `OverrideAnonymousEndpointAccessForGossip` to `true` allows anonymous access to the client gossip read operation specifically, overriding the other option.

| Format               | Syntax                                       |
|:---------------------|:---------------------------------------------|
| Command line         | `--allow-anonymous-endpoint-access`          |
| YAML                 | `AllowAnonymousEndpointAccess`               |
| Environment variable | `EVENTSTORE_ALLOW_ANONYMOUS_ENDPOINT_ACCESS` |

**Default**: `false`

| Format               | Syntax                                                     |
|:---------------------|:-----------------------------------------------------------|
| Command line         | `--override-anonymous-endpoint-access-for-gossip`          |
| YAML                 | `OverrideAnonymousEndpointAccessForGossip`                 |
| Environment variable | `EVENTSTORE_OVERRIDE_ANONYMOUS_ENDPOINT_ACCESS_FOR_GOSSIP` |

**Default**: `true`

::: tip
Anonymous access is still always granted to `/-/liveness`, `/-/readiness`, the static content of the UI, and HTTP redirects.
:::

### Certificates configuration

In this section, you can find settings related to protocol security (HTTPS and TLS).

#### Certificate common name

SSL certificates can be created with a common name (CN), which is an arbitrary string. Usually it contains the DNS name for which the certificate is issued.

When cluster nodes connect to each other, they need to ensure that they indeed talk to another node and not something that pretends to be a node. To achieve that, TrogonEventStore authenticates a connecting node by ensuring that it supplies a trusted client certificate having a CN that matches exactly with the CN in its own certificate. This essentially means that the CN must be the same across all node certificates by default. For example, generated local-development certificates commonly use `trogondb-node` in all node certificates.

::: note
Prior to version 23.10.0, the `CertificateReservedNodeCommonName` setting needed to be configured if a user had certificates with a CN other than `eventstoredb-node`. TrogonEventStore will now, by default, automatically read the CN from the node's certificate and use it as the `CertificateReservedNodeCommonName`. However, if you still choose to specify the `CertificateReservedNodeCommonName` in your configuration, it will take precedence.
:::

In practice, it's not always possible to obtain certificates where the CN is the same across all nodes. For instance, when using a public CA, single-domain certificates are very common and these certificates cannot be used on multiple nodes as they are valid for exactly one host. In this case, the `CertificateReservedNodeCommonName` setting can be configured with a wildcard as per the following example:

If the domains are `node1.esdb.mycompany.org`, `node2.esdb.mycompany.org` and `node3.esdb.mycompany.org`, then `CertificateReservedNodeCommonName` must be set to `*.esdb.mycompany.org`.

| Format               | Syntax                                             |
|:---------------------|:---------------------------------------------------|
| Command line         | `--certificate-reserved-node-common-name`          |
| YAML                 | `CertificateReservedNodeCommonName`                |
| Environment variable | `EVENTSTORE_CERTIFICATE_RESERVED_NODE_COMMON_NAME` |

::: warning
Server certificates **must** have the internal and external IP addresses (`ReplicationIp` and `NodeIp` respectively) or DNS names as subject alternative names.
:::

#### Trusted root certificates

When getting an incoming connection, the server needs to ensure if the certificate used for the connection can be trusted. For this to work, the server needs to know where trusted root certificates are located.

TrogonEventStore will use the default trusted root certificates location of `/etc/ssl/certs` when running on Linux only. So if you are running on Windows or a platform with a different default certificates location, you'd need to explicitly tell the node to use the OS default root certificate store. For certificates signed by a private CA, you just provide the path to the CA certificate file (but not the filename).

If you are running on Windows, you can also load the trusted root certificate from the Windows Certificate Store. The available options for configuring this are described [below](#certificate-store-windows).

| Format               | Syntax                                      |
|:---------------------|:--------------------------------------------|
| Command line         | `--trusted-root-certificates-path`          |
| YAML                 | `TrustedRootCertificatesPath`               |
| Environment variable | `EVENTSTORE_TRUSTED_ROOT_CERTIFICATES_PATH` |

**Default**: n/a on Windows, `/etc/ssl/certs` on Linux

#### Certificate file

The `CertificateFile` setting needs to point to the certificate file, which will be used by the cluster node.

| Format               | Syntax                        |
|:---------------------|:------------------------------|
| Command line         | `--certificate-file`          |
| YAML                 | `CertificateFile`             |
| Environment variable | `EVENTSTORE_CERTIFICATE_FILE` |

If the certificate file is protected by password, you'd need to set the `CertificatePassword` value accordingly, so the server can load the certificate.

| Format               | Syntax                            |
|:---------------------|:----------------------------------|
| Command line         | `--certificate-password`          |
| YAML                 | `CertificatePassword`             |
| Environment variable | `EVENTSTORE_CERTIFICATE_PASSWORD` |

If the certificate file doesn't contain the certificate private key, you need to tell the node where to find the key file using the `CertificatePrivateKeyFile` setting. The private key can be in RSA, or PKCS8 format.

| Format               | Syntax                                    |
|:---------------------|:------------------------------------------|
| Command line         | `--certificate-private-key-file`          |
| YAML                 | `CertificatePrivateKeyFile`               |
| Environment variable | `EVENTSTORE_CERTIFICATE_PRIVATE_KEY_FILE` |

If the private key file is an encrypted PKCS #8 file, then you need to provide the password with the `CertificatePrivateKeyPassword` option.

| Format               | Syntax                                        |
|:---------------------|:----------------------------------------------|
| Command line         | `--certificate-private-key-password`          |
| YAML                 | `CertificatePrivateKeyPassword`               |
| Environment variable | `EVENTSTORE_CERTIFICATE_PRIVATE_KEY_PASSWORD` |


#### Certificate store (Windows)

The certificate store location is the location of the Windows certificate store, for example `CurrentUser`.

| Format               | Syntax                                  |
|:---------------------|:----------------------------------------|
| Command line         | `--certificate-store-location`          |
| YAML                 | `CertificateStoreLocation`              |
| Environment variable | `EVENTSTORE_CERTIFICATE_STORE_LOCATION` |

The certificate store name is the name of the Windows certificate store, for example `My`.

| Format               | Syntax                              |
|:---------------------|:------------------------------------|
| Command line         | `--certificate-store-name`          |
| YAML                 | `CertificateStoreName`              |
| Environment variable | `EVENTSTORE_CERTIFICATE_STORE_NAME` |

You can load a certificate using either its thumbprint or its subject name.
If using the thumbprint, the server expects to only find one certificate file matching that thumbprint in the cert store.

| Format               | Syntax                              |
|:---------------------|:------------------------------------|
| Command line         | `--certificate-thumbprint`          |
| YAML                 | `CertificateThumbprint`             |
| Environment variable | `EVENTSTORE_CERTIFICATE_THUMBPRINT` |

The subject name matches any certificate that contains the specified name. This means that multiple matching certificates could be found.
Set it to the subject used by your node certificates, such as `trogondb-node` for a private development CA.

If multiple matching certificates are found, then the certificate with the latest expiry date will be selected.

| Format               | Syntax                                |
|:---------------------|:--------------------------------------|
| Command line         | `--certificate-subject-name`          |
| YAML                 | `CertificateSubjectName`              |
| Environment variable | `EVENTSTORE_CERTIFICATE_SUBJECT_NAME` |

When you are loading your node certificates from the Windows cert store, you are likely to want to load the trusted root certificate from the cert store as well.
The options to configure this are similar to the ones for node certificates.

The trusted root certificate store location is the location of the Windows certificate store in which the trusted root certificate is installed, for example `CurrentUser`.

| Format               | Syntax                                               |
|:---------------------|:-----------------------------------------------------|
| Command line         | `--trusted-root-certificate-store-location`          |
| YAML                 | `TrustedRootCertificateStoreLocation`                |
| Environment variable | `EVENTSTORE_TRUSTED_ROOT_CERTIFICATE_STORE_LOCATION` |

The trusted root certificate store name is the name of the Windows certificate store in which the trusted root certificate is installed, for example `Root`.

| Format               | Syntax                                           |
|:---------------------|:-------------------------------------------------|
| Command line         | `--trusted-root-certificate-store-name`          |
| YAML                 | `TrustedRootCertificateStoreName`                |
| Environment variable | `EVENTSTORE_TRUSTED_ROOT_CERTIFICATE_STORE_NAME` |

Trusted root certificates can also be loaded using either its thumbprint or its subject name.
If using the thumbprint, the server expects to only find one trusted root certificate file matching that thumbprint in the cert store.

| Format               | Syntax                                           |
|:---------------------|:-------------------------------------------------|
| Command line         | `--trusted-root-certificate-thumbprint`          |
| YAML                 | `TrustedRootCertificateThumbprint`               |
| Environment variable | `EVENTSTORE_TRUSTED_ROOT_CERTIFICATE_THUMBPRINT` |

The subject name matches any certificate that contains the specified name. This means that multiple matching certificates could be found.
Set it to the subject used by your trusted root certificate, such as `TrogonEventStore CA` for a private development CA.

If multiple matching root certificates are found, then the root certificate with the latest expiry date will be selected.

| Format               | Syntax                                             |
|:---------------------|:---------------------------------------------------|
| Command line         | `--trusted-root-certificate-subject-name`          |
| YAML                 | `TrustedRootCertificateSubjectName`                |
| Environment variable | `EVENTSTORE_TRUSTED_ROOT_CERTIFICATE_SUBJECT_NAME` |

### Certificate generation

Use your platform certificate tooling, public CA, or private CA process to create certificates that match your deployment. The built-in development mode can generate a certificate for one secure node on localhost, but it is not a cluster PKI bootstrap mechanism.

For a multi-node deployment, provision:

- A trusted CA certificate available to every node and client.
- A separate certificate and private key for each node.
- Subject alternative names for every DNS name and IP address used by clients or replication traffic.
- A common-name policy that matches `CertificateReservedNodeCommonName`.

Node certificates require Digital Signature plus either Key Encipherment or Key Agreement. If an Extended Key
Usage extension is present, it must contain both Server Authentication (`1.3.6.1.5.5.7.3.1`) and Client
Authentication (`1.3.6.1.5.5.7.3.2`). Certificates without an Extended Key Usage extension remain accepted for
compatibility.

Keep CA private keys outside the node and client environments. Install only the CA certificate, node certificate, and node private key required by each machine.

::: warning
Keep certificate private keys readable only by the account running TrogonEventStore. Restrictive permissions
reduce the risk of another local account reading the key.

You can do it by running the following command:

```bash
chmod 600 [file]
```
:::

### Certificate installation on a client environment

To connect to TrogonEventStore when using a private CA, install its certificate on the client machine, such as the machine where the client is hosted or your development environment.

#### Linux (Ubuntu, Debian)

1. Copy the private CA certificate to `/usr/local/share/ca-certificates/`, for example:
  ```bash
  sudo cp ca.crt /usr/local/share/ca-certificates/event_store_ca.crt
  ```
2. Update the CA store:
  ```bash
  sudo update-ca-certificates
  ```

#### Windows

1. You can manually import it to the local CA cert store through `Certificates Local Machine Management Console`. To do that select **Run** from the **Start** menu, and then enter `certmgr.msc`. Then import certificate to `Trusted Root Certification`.
2. You can also run the PowerShell script instead:

```powershell
Import-Certificate -FilePath ".\certs\ca\ca.crt" -CertStoreLocation Cert:\CurrentUser\Root
```

#### MacOS

1. In the Keychain Access app on your Mac, select either the login or System keychain. Drag the certificate file onto the Keychain Access app. If you're asked to provide a name and password, type the name and password for an administrator user on this computer.
2. You can also run the bash script:

```bash
sudo security add-certificates -k /Library/Keychains/System.keychain ca.crt 
```

### Intermediate CA certificates

Intermediate CA certificates are supported by loading them from a [PEM](https://datatracker.ietf.org/doc/html/rfc1422) or [PKCS #12](https://datatracker.ietf.org/doc/html/rfc7292) bundle specified by the [`CertificateFile` configuration parameter](#certificate-file). To make sure that the configuration is correct, the certificate chain is validated on startup with the node's own certificate.

If your root CA directly signed the node certificate, then the certificate chain has no intermediate CA certificate.

However, if you're using a public certificate authority (e.g [Let's Encrypt](https://letsencrypt.org/)) to generate your node certificates there is a chance that you're using intermediate CA certificates without knowing. This is due to the [Authority Information Access (AIA)](https://datatracker.ietf.org/doc/html/rfc4325#section-2) extension which allows intermediate certificates to be fetched from a remote server.

To verify if your certificate is using the AIA extension, you need to verify if there is a section named: `Authority Information Access` in the certificate.

:::: tabs
::: tab Linux

Use `openssl` to find the section in the certificate file:

```
openssl x509 -in /path/to/node.crt -text | grep 'Authority Information Access' -A 1
```
:::
::: tab Windows
Open the certificate, go to the `Details` tab and look for the `Authority Information Access` field.

If the extension is present, you can manually download the intermediate certificate from the URL present under the `CA Issuers` entry.
Note that you will usually need to convert the downloaded certificate from the `DER` to the `PEM` format.

This can be done with the following `openssl` command:

```bash
openssl x509 -inform der -in /path/to/cert.der > /path/to/cert.pem
```

or with [an online service](https://www.sslshopper.com/ssl-converter.html) if you don't have openssl installed.
:::
::::

It's possible that there are more than one intermediate CA certificates in the chain - so you need to verify if the certificate you've just downloaded also uses the AIA extension. If yes, you need to download the next intermediate CA certificate in the chain by repeating the same process above until you eventually reach a publicly trusted root certificate (i.e. the `Subject` and `Issuer` fields will match). In practice, there'll usually be at most two intermediate certificates in the chain.

#### Bundling the intermediate certificates

The node's certificate should be first in the bundle, followed by the intermediates. Intermediates can be in any order but it would be good to keep it from leaf to root, as per the usual convention. The root certificate should not be bundled.

In the examples below, intermediate certificates are numbered from 1 to N starting from the leaf and going up.

##### PEM format

If your node's certificate and the intermediate CA certificates are both PEM formatted, that is they begin with `-----BEGIN CERTIFICATE-----` and end with `-----END CERTIFICATE-----` then you can simply append the contents of the intermediate certificate files to the end of the node's certificate file to create the bundle.

:::: code-group
::: code-group-item Linux

```bash
cat /path/to/intermediate1.crt >> /path/to/node.crt
...
cat /path/to/intermediateN.crt >> /path/to/node.crt
```

:::
::: code-group-item Windows

```powershell
type C:\path\to\intermediate1.crt >> C:\path\to\node.crt
...
type C:\path\to\intermediateN.crt >> C:\path\to\node.crt
```
:::
::::

##### PKCS #12 format

If you want to generate a PKCS #12 bundle from PEM formatted certificate files, please follow the steps below.

```bash
cat /path/to/intermediate1.crt >> ./ca_bundle.crt
...
cat /path/to/intermediateN.crt >> ./ca_bundle.crt

openssl pkcs12 -export -in /path/to/node.crt -inkey /path/to/node.key -certfile ./ca_bundle.crt -out /path/to/node.p12 -passout pass:<password>
```

#### Adding intermediate certificates to the certificate store

Intermediate certificates also need to be added to the current user's certificate store.

This is required for two reasons:  
i)  For the full certificate chain to be sent when TLS connections are established  
ii) To improve performance by preventing certificate downloads if your certificate uses the AIA extension  

:::: tabs
::: tab Linux
The following script assumes TrogonEventStore is running under the same service account that owns the node process.

```bash
sudo su <service-account> --shell /bin/bash
dotnet tool install --global dotnet-certificate-tool
 ~/.dotnet/tools/certificate-tool add -s CertificateAuthority -l CurrentUser --file /path/to/intermediate.crt
```
:::
::: tab Windows
To import the intermediate certificate in the `Intermediate Certification Authorities` certificate store, run the following PowerShell command under the same account as TrogonEventStore is running:

```powershell
Import-Certificate -FilePath .\path\to\intermediate.crt -CertStoreLocation Cert:\CurrentUser\CA
```

Optionally, to import the intermediate certificate in the `Local Computer` store, run the following as `Administrator`:

```powershell
Import-Certificate -FilePath .\ca.crt -CertStoreLocation Cert:\LocalMachine\CA
```
:::
::::

### Replication protocol security

When TLS is enabled, cluster replication uses the configured node certificate. Replication TLS cannot be
disabled independently. Use [`DisableTls`](#disable-tls) to disable transport encryption for both HTTP and
replication while preserving authentication and authorization.

## Authentication

TrogonEventStore supports authentication based on usernames and passwords out of the box. Nodes can also enable
OAuth bearer-token authentication as a built-in authentication method. This follows PostgreSQL's model of
making the database authentication method explicit, so password and OAuth access can be reasoned about side by side.

Authentication is applied to all HTTP endpoints by default, except `/-/liveness`, `/-/readiness`, static web
content, and redirects.

### Authentication methods

Use `Auth:Methods` to choose the authentication methods enabled by the node. If `Auth:Methods` is not set, the
legacy `Auth:AuthenticationType` setting is still honored for compatibility.

| Format               | Syntax                      |
|:---------------------|:----------------------------|
| Command line         | `--auth:methods:0 password --auth:methods:1 oauth` |
| YAML                 | `Auth:Methods`              |
| Environment variable | `EVENTSTORE__AUTH__METHODS__0`, `EVENTSTORE__AUTH__METHODS__1`, ... |

Supported built-in values are:

- `password` enables username and password authentication against the node user store.
- `oauth` enables bearer-token authentication against the configured OAuth or OIDC issuer.

For example, to enable both methods:

```yaml
Auth:
  Methods:
    - password
    - oauth
```

### OAuth authentication

OAuth authentication validates bearer tokens issued by the configured OAuth or OIDC identity provider. The node
requires an issuer and at least one accepted audience.

```yaml
Auth:
  Methods:
    - password
    - oauth
  OAuth:
    Issuer: https://identity.example.com
    Audiences:
      - eventstore
```

The browser sign-in flow is available when the OAuth authorization endpoint, token endpoint, client id, and scopes
are configured. The flow stores only access tokens that the node can validate as JWT bearer tokens.

```yaml
Auth:
  OAuth:
    AuthorizationEndpoint: https://identity.example.com/oauth2/authorize
    TokenEndpoint: https://identity.example.com/oauth2/token
    ClientId: eventstore-ui
    Scopes:
      - openid
      - profile
      - email
```

### Default users

TrogonEventStore provides two default users, `$ops` and `$admin`.

`$admin` has full access to everything in TrogonEventStore. It can read and write to protected streams, which is
any stream that starts with \$, such as `$projections-master`. Protected streams are usually system streams,
for example, `$projections-master` manages some projections' states. The `$admin` user can also run
operational commands, such as scavenges and shutdowns on TrogonEventStore.

An `$ops` user can do everything that an `$admin` can do except manage users and read from system streams (
except for `$scavenges` and `$scavenges-streams`).

### New users

New users created in TrogonEventStore are standard non-admin users. They can call endpoints permitted by the
authorization policy, including default anonymous endpoints such as `/-/liveness` and `/-/readiness`.

Internal cluster endpoints, such as election and gossip updates, are only available to system node traffic.

By default, any user can read any non-protected stream unless there is an ACL preventing that.

### Externalised authentication

You can also use the trusted intermediary header for externalized authentication that allows you to integrate
almost any authentication system with TrogonEventStore. Read more
about [the trusted intermediary header](#trusted-intermediary).

## Access control lists

By default, authenticated users have access to the whole TrogonEventStore database. In addition to that, it allows
you to use Access Control Lists (ACLs) to set up more granular access control. In fact, the default access
level is also controlled by a special ACL, which is called the [default ACL](#default-acl).

### Stream ACL

TrogonEventStore keeps the ACL of a stream in the stream metadata as JSON with the below definition:

```json
{
  "$acl": {
    "$w": "$admins",
    "$r": "$all",
    "$d": "$admins",
    "$mw": "$admins",
    "$mr": "$admins"
  }
}
```

These fields represent the following:

- `$w` The permission to write to this stream.
- `$r` The permission to read from this stream.
- `$d` The permission to delete this stream.
- `$mw` The permission to write the metadata associated with this stream.
- `$mr` The permission to read the metadata associated with this stream.

You can update these fields with either a single string or an array of strings representing users or
groups (`$admins`, `$all`, or custom groups). It's possible to put an empty array into one of these fields,
and this has the effect of removing all users from that permission.

::: tip 
We recommend you don't give people access to `$mw` as then they can then change the ACL.
:::

### Default ACL

The `$settings` stream has a special ACL used as the default ACL. This stream controls the default ACL for
streams without an ACL and also controls who can create streams in the system, the default state of these is
shown below:

```json
{
  "$userStreamAcl": {
    "$r": "$all",
    "$w": "$all",
    "$d": "$all",
    "$mr": "$all",
    "$mw": "$all"
  },
  "$systemStreamAcl": {
    "$r": "$admins",
    "$w": "$admins",
    "$d": "$admins",
    "$mr": "$admins",
    "$mw": "$admins"
  }
}
```

You can rewrite these to the `$settings` stream with a gRPC client or from the Admin UI stream browser.

The `$userStreamAcl` controls the default ACL for user streams, while all system streams use
the `$systemStreamAcl` as the default.

::: tip 
The `$w` in `$userStreamAcl` also applies to the ability to create a stream. Members of `$admins`
always have access to everything, you cannot remove this permission.
:::

When you set a permission on a stream, it overrides the default values. However, it's not necessary to specify
all permissions on a stream. It's only necessary to specify those which differ from the default.

Here is an example of the default ACL that has been changed:

```json
{
  "$userStreamAcl": {
    "$r": "$all",
    "$w": "ouro",
    "$d": "ouro",
    "$mr": "ouro",
    "$mw": "ouro"
  },
  "$systemStreamAcl": {
    "$r": "$admins",
    "$w": "$admins",
    "$d": "$admins",
    "$mr": "$admins",
    "$mw": "$admins"
  }
}
```

This default ACL gives `ouro` and `$admins` create and write permissions on all streams, while everyone else
can read from them. Be careful allowing default access to system streams to non-admins as they would also have
access to `$settings` unless you specifically override it.

Refer to the documentation of the SDK of your choice for more information about changing ACLs programmatically.

## Trusted intermediary

The trusted intermediary header helps TrogonEventStore to support a common security architecture. There are
thousands of possible methods for handling authentication and it is impossible for us to support them all. The
header allows you to configure a trusted intermediary to handle the authentication instead of TrogonEventStore.

A sample configuration is to enable OAuth2 with the following steps:

- Configure TrogonEventStore to run on the local loopback.
- Configure nginx to handle OAuth2 authentication.
- After authenticating the user, nginx rewrites the request and forwards it to the loopback to TrogonEventStore
  that serves the request.

The header has the form of `{user}; group, group1` and the TrogonEventStore ACLs use the information to handle
security.

```http:no-line-numbers
ES-TrustedAuth: "root; admin, other"
```

Use the following option to enable this feature:

| Format               | Syntax                           |
|:---------------------|:---------------------------------|
| Command line         | `--enable-trusted-auth`          |
| YAML                 | `EnableTrustedAuth`              |
| Environment variable | `EVENTSTORE_ENABLE_TRUSTED_AUTH` |

## Supported authentication boundary

TrogonEventStore documents the authentication methods built into this
distribution:

- Local username and password authentication.
- OAuth methods configured in the server.
- Trusted intermediary authentication for deployments that terminate
  authentication in a trusted local proxy.

Do not configure undocumented authentication plugins as part of the supported
security model. If a deployment needs directory integration or certificate-based
user identity, model that requirement through OAuth, a trusted intermediary, or
a separate product decision before exposing it as server documentation.
