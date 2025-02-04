MySQL-Clone
---

A simple .net CLI utility to clone a mysql database

Quick usage
---
```
npm ci
```

_ask the tool how to use it:_
```
npm start -- -- --help
```
(all the `--`s are required to get past npm 🙄)

_run interactively: answer some questions and get a cloned database:_
```
npm run interactive
```

_run like an expert:_
```
npm start -- -h source-host -u source-user -p source-password -d source-database -H target-host -U target-user -P target-password -D target database
```

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
