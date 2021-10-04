MySQL-Clone
---

A simple .net CLI utility to clone a mysql database

Build
---
If you have dotnet core 5 and node installed, you should be able to:
```
npm ci
npm run build
```

to produce binaries in the `bin` folder. Alternatively, use the binaries that are checked in,
if you trust me. Or build with your IDE of choice (VS, Rider, etc)

Usage
---
Run `mysql-clone.exe` with `--help` for all options or with `-i` for a guided interactive session

Why
---
I wanted to automate away the steps of cloning a mysql database using `mysqldump` and `mysql`,
including the ability to run a clean-up script after restoration completes.
