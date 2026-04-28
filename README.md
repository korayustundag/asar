# Asar

[![License](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](LICENSE)
[![Target Framework](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512bd4.svg?style=flat-square)](https://dotnet.microsoft.com/)

**Asar** is a high-performance, full-featured C# library designed for the modern .NET ecosystem. It provides a robust and intuitive API for reading, writing, and manipulating ASAR (Atom Shell Archive) files—the standard packaging format for Electron applications.

Built with a focus on performance and developer productivity, **Asar** offers first-class support for random-access streaming, asynchronous I/O, and thread-safe operations, making it suitable for everything from simple extraction tools to complex build pipelines.

---

## Key Features

- **🚀 High Performance:** Memory-efficient streaming and O(1) path resolution.
- **🧵 Thread-Safe:** Concurrent random-access reads powered by shared stream locking.
- **🔄 Full Lifecycle Support:** Create, Read, Update, and Delete (CRUD) operations on archives.
- **⚡ Async-First:** Comprehensive `Task`-based APIs for responsive applications.
- **📦 Advanced Packing:** Intelligent directory packing with glob filtering and native module (.unpacked) support.
- **🛡️ Data Integrity:** Preservation of Electron integrity blocks and POSIX executable bits.
- **💎 Multi-Target:** Optimized for .NET 8, 9, and 10.

---

## Getting Started

### Installation

Add the **Asar** package to your project via the NuGet CLI:

```bash
dotnet add package Asar
```

### Quick Start

#### Read an existing archive
```csharp
using Asar.Core;

// Open an archive for reading
using var archive = AsarArchive.Open("app.asar");

// Read file content directly
string config = archive.ReadAllText("config.json");

// Or use high-performance streaming for large files
using var stream = archive.OpenEntryStream("assets/video.mp4");
```

#### Modify and save
```csharp
using var archive = AsarArchive.Open("app.asar");

// Update or add new files
archive.AddFile("main.js", "console.log('Patched!');");
archive.RemoveFile("old-asset.png");

// Save the changes
archive.Save("app-updated.asar");
```

#### Pack a directory
```csharp
// Pack an entire folder with default Electron conventions
AsarPacker.Pack("./src", "app.asar");
```

---

## Core Components

| Component | Responsibility |
| :--- | :--- |
| **`AsarArchive`** | The primary façade for reading, modifying, and saving archives. |
| **`AsarReader`** | Low-level, thread-safe reader providing random-access to entries. |
| **`AsarWriter`** | High-level builder for creating new archives from various sources. |
| **`AsarPacker`** | Specialized tool for converting directory trees into archives. |

---

## Why Asar?

### Random-Access Guarantee
Unlike many archive libraries that require sequential scanning, **Asar** parses the archive header into an in-memory tree. This enables true random access: you can `Seek` to any byte in any file without reading the preceding data.

### Optimized for Large Data
Whether you are working with a few kilobytes or several gigabytes, **Asar** maintains a constant memory footprint by leveraging modern .NET `Stream` and `Span<byte>` APIs.

### Electron Compatibility
**Asar** is designed to be fully compatible with the official Electron `asar` tool. It correctly handles the companion `.unpacked` directories required for native `.node` modules and preserves file metadata across platforms.

---

## Documentation

For more information, technical documentation, and usage examples, please visit the [wiki](https://github.com/korayustundag/asar/wiki) page.

---

## Feedback & Contribution

This project is maintained by **Koray USTUNDAG**. If you encounter any issues or have feature requests, please open an issue on the [GitHub repository](https://github.com/korayustundag/asar).

---

## License

The **Asar** library is licensed under the [MIT License](LICENSE).

Copyright © 2026 Koray USTUNDAG
