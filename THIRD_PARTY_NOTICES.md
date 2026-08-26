# Third-party notices

AgentNotify incorporates open-source components. This file is informational and does not replace the license terms included by each component.

## Runtime components

| Component | Version | License | Project |
|---|---:|---|---|
| MailKit | 4.17.0 | MIT | https://github.com/jstedfast/MailKit |
| MimeKit | 4.17.0 | MIT | https://github.com/jstedfast/MimeKit |
| Bouncy Castle Cryptography | 2.6.2 | MIT | https://www.bouncycastle.org/csharp/ |
| MQTTnet | 5.2.0.1603 | MIT | https://github.com/dotnet/MQTTnet |
| Microsoft.Data.Sqlite and Microsoft cryptography packages | 10.0.x | MIT | https://github.com/dotnet/efcore and https://github.com/dotnet/runtime |
| SQLite / SQLitePCLRaw | 3.x / 2.1.x | Public domain / Apache-2.0 | https://sqlite.org and https://github.com/ericsink/SQLitePCL.raw |

Build/test-only dependencies such as xUnit, Microsoft.NET.Test.Sdk, and coverlet are not shipped as application runtime libraries. Their package metadata remains available through NuGet restore.

## Documentation site components

The generated GitHub Pages site includes or is built with the following open-source components.
Exact transitive versions and integrity hashes are recorded in `site/package-lock.json`; installed
packages retain their complete licence files and metadata.

| Component | Version | License | Project |
|---|---:|---|---|
| Next.js | 16.3.3 | MIT | https://github.com/vercel/next.js |
| React / React DOM | 19.2.8 | MIT | https://github.com/facebook/react |
| shadcn/ui source components | current checked-in source | MIT | https://github.com/shadcn-ui/ui |
| Radix UI Dialog / Slot | 1.1.23 / 1.3.3 | MIT | https://github.com/radix-ui/primitives |
| Lucide React | 1.34.0 | ISC | https://github.com/lucide-icons/lucide |
| Tailwind CSS / PostCSS | 4.3.3 / 8.5.26 | MIT | https://github.com/tailwindlabs/tailwindcss / https://github.com/postcss/postcss |
| class-variance-authority | 0.7.1 | Apache-2.0 | https://github.com/joe-bell/cva |
| clsx / tailwind-merge | 2.1.1 / 3.6.0 | MIT | https://github.com/lukeed/clsx / https://github.com/dcastil/tailwind-merge |
| unified, remark, rehype, and unist utilities | versions in lockfile | MIT | https://github.com/unifiedjs |

TypeScript and the `@types/*` packages are build-only and do not ship in the static export.

## MailKit

MIT License

Copyright (C) 2013-2026 .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## MimeKit

MIT License

Copyright (C) 2012-2026 .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## MQTTnet

The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors
All Rights Reserved

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
