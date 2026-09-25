#!/bin/sh

set -eu

script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
cert_root=$(mktemp -d)
trap 'rm -r -- "$cert_root"' EXIT

sh "$script_dir/generate-tls-certificates.sh" "$cert_root"

openssl pkcs12 -in "$cert_root/public/node.p12" -nokeys -passin pass: \
	-out "$cert_root/bundle.pem" >/dev/null 2>&1

bundle_count=$(grep -c '^-----BEGIN CERTIFICATE-----' "$cert_root/bundle.pem")
if [ "$bundle_count" -ne 1 ]; then
	echo "Expected only the node certificate in node.p12; found $bundle_count certificates" >&2
	exit 1
fi
openssl verify -CAfile "$cert_root/public/ca/ca.crt" \
	"$cert_root/private/node.crt" >/dev/null
