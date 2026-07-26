# OpenSSH AskPass helper trust gate

`WinARD.OpenSshAskPass.exe` is resolved only from the absolute application directory. WinARD never searches the current directory or `PATH`, and the broker refuses a helper with a different file name or parent directory.

Task 16 packaging must add an Authenticode gate before production release:

1. Sign the desktop application and `WinARD.OpenSshAskPass.exe` with the same release certificate.
2. Verify the helper signature, certificate chain, expected publisher, and file hash before creating an askpass session.
3. Fail closed with a non-secret error code when verification fails.
4. Install the helper beside the application with ACLs that prevent unprivileged replacement.

The MVP path check prevents current-directory and `PATH` hijacking. It is not a substitute for the Task 16 signature verification gate.

The helper sends one fixed, length-prefixed request frame in a single write. The
broker reads exactly that frame, authenticates its challenge, and permits only
one successful consumption. Byte-stream pipes cannot prove that no byte will
arrive after an already complete frame, so late trailing bytes are ignored for
that consumed connection and are not treated as an authentication check.
