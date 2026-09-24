#!/bin/sh

set -eu

cert_root=${1:?certificate output directory is required}
config_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)

umask 077
mkdir -p "$cert_root/private" "$cert_root/public/ca"

openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 1 \
	-keyout "$cert_root/private/ca.key" \
	-out "$cert_root/public/ca/ca.crt" \
	-subj '/CN=Compatibility Test Root' \
	-extensions v3_ca -config "$config_dir/tls.cnf" >/dev/null 2>&1

openssl req -new -newkey rsa:2048 -nodes -sha256 \
	-keyout "$cert_root/private/node.key" \
	-out "$cert_root/private/node.csr" \
	-subj '/CN=localhost' -config "$config_dir/tls.cnf" >/dev/null 2>&1

openssl x509 -req -sha256 -days 1 \
	-in "$cert_root/private/node.csr" \
	-CA "$cert_root/public/ca/ca.crt" \
	-CAkey "$cert_root/private/ca.key" \
	-CAcreateserial \
	-out "$cert_root/private/node.crt" \
	-extensions v3_node -extfile "$config_dir/tls.cnf" >/dev/null 2>&1

openssl pkcs12 -export \
	-inkey "$cert_root/private/node.key" \
	-in "$cert_root/private/node.crt" \
	-out "$cert_root/public/node.p12" \
	-passout pass: >/dev/null 2>&1

openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 1 \
	-keyout "$cert_root/private/untrusted-ca.key" \
	-out "$cert_root/public/untrusted-ca.crt" \
	-subj '/CN=Untrusted Compatibility Test Root' \
	-extensions v3_ca -config "$config_dir/tls.cnf" >/dev/null 2>&1

chmod 0755 "$cert_root" "$cert_root/public" "$cert_root/public/ca"
chmod 0644 "$cert_root/public/node.p12" "$cert_root/public/ca/ca.crt" "$cert_root/public/untrusted-ca.crt"
