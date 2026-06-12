#!/bin/sh
# Genera UNA VOLTA il certificato TLS self-signed dell'emulatore Key Vault nel volume condiviso
# (job one-shot `keyvault-certs` di docker-compose). L'SDK Azure pretende https, quindi l'emulatore
# espone TLS: il trust del self-signed resta SCOPED al client dell'app in modalità Emulator — nessun
# trust store di host/container viene toccato. Vedi docs/keyvault.md.
#
# Password del PFX = 'emulator': è il CONTRATTO dell'immagine dell'emulatore (ENV Kestrel nel suo
# Dockerfile si aspetta /certs/emulator.pfx con quella password), non un segreto.
set -eu

if [ -f /certs/emulator.pfx ]; then
  echo "emulator.pfx già presente nel volume: niente da rigenerare."
  exit 0
fi

# SAN: 'keyvault' è l'hostname del servizio sulla rete compose, 'localhost' per l'ispezione dall'host.
openssl req -x509 -newkey rsa:2048 -sha256 -days 365 -nodes \
  -keyout /tmp/emulator.key -out /tmp/emulator.crt \
  -subj "/CN=localhost" \
  -addext "subjectAltName=DNS:localhost,DNS:keyvault" \
  -addext "extendedKeyUsage=serverAuth"

openssl pkcs12 -export -out /certs/emulator.pfx \
  -inkey /tmp/emulator.key -in /tmp/emulator.crt -passout pass:emulator

echo "Certificato emulator.pfx generato nel volume."
