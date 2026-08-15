FROM alpine:3.22.1@sha256:4bcff63911fcb4448bd4fdacec207030997caf25e9bea4045fa6c8c44de311d1

RUN apk add --no-cache openssl=3.5.7-r0

COPY --chmod=755 scripts/certificates/generate-cluster-certificates.sh /usr/local/bin/generate-cluster-certificates

ENTRYPOINT ["/usr/local/bin/generate-cluster-certificates"]
