"""Reproduce WinARD's independent ARD type 30 known-answer vector.

Run from the repository root:
    python -m pip install cryptography
    python tools/verify_ard_known_answer.py
"""

import sys
from hashlib import md5, sha256

try:
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
except ImportError:
    print(
        "Missing dependency: install it with 'python -m pip install cryptography'.",
        file=sys.stderr,
    )
    raise SystemExit(2)


KEY_LENGTH = 64
MODULUS = int(
    "D2652EF10104A3DDC1219700EDFBD1E19F7678B4A4F6D5952634BD8BF1D60326"
    "322B5D32366DC25CB4E8E73AF4312A70D2DCAF2747EB89D7E88553EECD6A283D",
    16,
)
GENERATOR = 5
SERVER_PRIVATE = 3
CLIENT_PRIVATE = 2
EXPECTED_CLIENT_PUBLIC_HEX = (
    "0000000000000000000000000000000000000000000000000000000000000000"
    "0000000000000000000000000000000000000000000000000000000000000019"
)
EXPECTED_CIPHERTEXT_HEX = (
    "9AA256017F35EF39E53F21F3ADC120050F1E37C126550C7BB942E4309C7B5A676"
    "8C180417D0A5EE5C7B96A1BEFC94836A05C01FA873DEAEF39AEFA78181F424D3"
    "5DC1F3264BB5EDC9FC85C63466B02763EF1168ECC0CD7F0F39079C2E85773102"
    "20D7936D1A4A6A1FE6B3755B09FBCBF94BDD64C681EE61628876756EC241E19"
)
EXPECTED_RESPONSE_SHA256 = (
    "b83e83d40a70f71e00d3ba48c93e12ca5172b4ab364040d293ca9a23de4a1fa5"
)


server_public = pow(GENERATOR, SERVER_PRIVATE, MODULUS)
client_public = pow(GENERATOR, CLIENT_PRIVATE, MODULUS)
shared_secret = pow(server_public, CLIENT_PRIVATE, MODULUS).to_bytes(
    KEY_LENGTH, "big"
)
aes_key = md5(shared_secret).digest()  # Protocol compatibility, not a modern KDF.

# This deterministic pattern proves that bytes after each NUL remain intact.
plaintext = bytearray(range(128))
plaintext[0:4] = b"user"
plaintext[4] = 0
plaintext[64:72] = b"password"
plaintext[72] = 0

encryptor = Cipher(algorithms.AES(aes_key), modes.ECB()).encryptor()
ciphertext = encryptor.update(bytes(plaintext)) + encryptor.finalize()
client_public_bytes = client_public.to_bytes(KEY_LENGTH, "big")
response = ciphertext + client_public_bytes

assert client_public_bytes.hex().upper() == EXPECTED_CLIENT_PUBLIC_HEX
assert ciphertext.hex().upper() == EXPECTED_CIPHERTEXT_HEX
assert sha256(response).hexdigest() == EXPECTED_RESPONSE_SHA256

print(f"client_public={client_public_bytes.hex().upper()}")
print(f"ciphertext={ciphertext.hex().upper()}")
print(f"response_sha256={sha256(response).hexdigest()}")
