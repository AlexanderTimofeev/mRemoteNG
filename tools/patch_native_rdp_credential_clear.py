from pathlib import Path

path = Path("mRemoteNG/UI/Controls/ConnectionContextMenu.cs")
text = path.read_text(encoding="utf-8-sig")
old = "ClearCachedCredentialsResult outcome = RdpCredentialCacheCleaner.ClearCachedCredentials(hostname);"
new = "ClearCachedCredentialsResult outcome = RdpCredentialCacheCleaner.ClearCachedCredentials(selected);"
count = text.count(old)
if count != 1:
    raise RuntimeError(f"Expected one credential cleaner call, found {count}")
path.write_text(text.replace(old, new, 1), encoding="utf-8", newline="\n")
