# Vault KDF cancellation

The Argon2id library used by WinARD does not cancel a derivation after it has started. WinARD therefore serializes derivations through one process-wide gate and enforces strict file-format limits before starting Argon2id.

If caller cancellation arrives during a derivation, WinARD waits for and observes the bounded underlying operation before zeroing the password buffer and releasing the gate. Any abandoned derived key is immediately zeroed. This deliberately favors secret-buffer lifetime safety and bounded resource usage over immediate cancellation completion.
