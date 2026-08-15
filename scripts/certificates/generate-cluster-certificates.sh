#!/usr/bin/env sh

set -eu

output_directory="${CERT_OUTPUT_DIRECTORY:-/certs}"
ca_days="${CERT_CA_DAYS:-3650}"
node_days="${CERT_NODE_DAYS:-825}"
node_common_name="${CERT_NODE_COMMON_NAME:-trogondb-node}"
output_owner="${CERT_OUTPUT_OWNER:-10000:10000}"

expected_files="
$output_directory/ca/ca.crt
$output_directory/node1/node.crt
$output_directory/node1/node.key
$output_directory/node2/node.crt
$output_directory/node2/node.key
$output_directory/node3/node.crt
$output_directory/node3/node.key
"

validate_node() {
	node_name="$1"
	node_ip="$2"
	node_directory="$output_directory/$node_name"

	openssl verify -CAfile "$output_directory/ca/ca.crt" "$node_directory/node.crt" >/dev/null
	openssl verify -purpose sslserver -CAfile "$output_directory/ca/ca.crt" "$node_directory/node.crt" >/dev/null
	openssl verify -purpose sslclient -CAfile "$output_directory/ca/ca.crt" "$node_directory/node.crt" >/dev/null
	openssl x509 -in "$node_directory/node.crt" -noout -checkend 0 >/dev/null
	openssl x509 -in "$node_directory/node.crt" -noout -checkhost localhost >/dev/null
	openssl x509 -in "$node_directory/node.crt" -noout -checkhost "esdb-$node_name" >/dev/null
	openssl x509 -in "$node_directory/node.crt" -noout -checkip 127.0.0.1 >/dev/null
	openssl x509 -in "$node_directory/node.crt" -noout -checkip "$node_ip" >/dev/null
	test "$(openssl x509 -in "$node_directory/node.crt" -noout -subject -nameopt RFC2253)" = "subject=CN=$node_common_name"

	certificate_public_key="$(mktemp)"
	private_public_key="$(mktemp)"
	openssl x509 -in "$node_directory/node.crt" -pubkey -noout >"$certificate_public_key"
	openssl pkey -in "$node_directory/node.key" -pubout >"$private_public_key" 2>/dev/null
	cmp "$certificate_public_key" "$private_public_key" >/dev/null
	rm -f "$certificate_public_key" "$private_public_key"
}

normalize_output_permissions() {
	chown -R "$output_owner" "$output_directory"
	chmod 755 "$output_directory" "$output_directory/ca"
	chmod 700 "$output_directory"/node*
	chmod 600 "$output_directory"/node*/node.key
	chmod 644 "$output_directory/ca/ca.crt" "$output_directory"/node*/node.crt
}

validate_existing_certificates() {
	openssl verify -CAfile "$output_directory/ca/ca.crt" "$output_directory/ca/ca.crt" >/dev/null
	validate_node node1 172.30.240.11
	validate_node node2 172.30.240.12
	validate_node node3 172.30.240.13
}

existing_files=0
missing_files=0

for output_path in "$output_directory" "$output_directory/ca" "$output_directory/node1" "$output_directory/node2" "$output_directory/node3"; do
	if [ -L "$output_path" ] || { [ -e "$output_path" ] && [ ! -d "$output_path" ]; }; then
		echo "Certificate output path '$output_path' must be a real directory, not a link or another file type." >&2
		exit 1
	fi
done

if [ -d "$output_directory/ca" ]; then
	unexpected_ca_entry="$(find "$output_directory/ca" -mindepth 1 -maxdepth 1 ! -name ca.crt -print -quit)"
	if [ -n "$unexpected_ca_entry" ]; then
		echo "Unexpected content exists in '$output_directory/ca'. Remove the local certificate directory and regenerate it so only the public ca.crt is exposed to nodes." >&2
		exit 1
	fi
fi

for expected_file in $expected_files; do
	if [ -L "$expected_file" ] || { [ -e "$expected_file" ] && [ ! -f "$expected_file" ]; }; then
		echo "Certificate output '$expected_file' must be a regular file, not a link or another file type." >&2
		exit 1
	elif [ -f "$expected_file" ]; then
		existing_files=$((existing_files + 1))
	else
		missing_files=$((missing_files + 1))
	fi
done

if [ "$existing_files" -gt 0 ]; then
	if [ "$missing_files" -gt 0 ]; then
		echo "Certificate output is incomplete. Remove '$output_directory' before regenerating it." >&2
		exit 1
	fi

	normalize_output_permissions
	validate_existing_certificates
	echo "Using the existing validated cluster certificates in '$output_directory'."
	exit 0
fi

umask 077
mkdir -p "$output_directory/ca" "$output_directory/node1" "$output_directory/node2" "$output_directory/node3"
private_directory="$(mktemp -d)"
trap 'rm -rf "$private_directory"' EXIT
ca_key="$private_directory/ca.key"

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$ca_key" 2>/dev/null
openssl req -x509 -new -sha256 \
	-key "$ca_key" \
	-out "$output_directory/ca/ca.crt" \
	-days "$ca_days" \
	-subj "/CN=TrogonEventStore Development CA" \
	-addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
	-addext "keyUsage=critical,keyCertSign,cRLSign" \
	-addext "subjectKeyIdentifier=hash"

generate_node() {
	node_name="$1"
	node_ip="$2"
	serial_number="$3"
	node_directory="$output_directory/$node_name"
	extension_file="$private_directory/$node_name.extensions"
	request_file="$private_directory/$node_name.csr"

	cat >"$extension_file" <<EOF
[node]
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth,clientAuth
subjectKeyIdentifier=hash
authorityKeyIdentifier=keyid,issuer
subjectAltName=DNS:localhost,DNS:esdb-$node_name,IP:127.0.0.1,IP:$node_ip
EOF

	openssl req -new -newkey rsa:3072 -nodes -sha256 \
		-keyout "$node_directory/node.key" \
		-out "$request_file" \
		-subj "/CN=$node_common_name" 2>/dev/null
	openssl x509 -req -sha256 \
		-in "$request_file" \
		-CA "$output_directory/ca/ca.crt" \
		-CAkey "$ca_key" \
		-set_serial "$serial_number" \
		-days "$node_days" \
		-extfile "$extension_file" \
		-extensions node \
		-out "$node_directory/node.crt"
}

generate_node node1 172.30.240.11 1001
generate_node node2 172.30.240.12 1002
generate_node node3 172.30.240.13 1003

normalize_output_permissions
validate_existing_certificates
echo "Generated and validated cluster certificates in '$output_directory'."
