# EasyServiceManager (Easy Service Diagnostics)

[![Microsoft Store](https://img.shields.io/badge/Microsoft%20Store-Windows%2010%2F11-10b981)](https://apps.microsoft.com/detail/9mxt54r13d17?hl=en&gl=US&ocid=pdpshare)
[![Linux / CLI](https://img.shields.io/badge/Linux-GUI%20%26%20CLI%20(esd)-0284c7)](#-download)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

English | [日本語](README.md)

A local-first service discovery and quick security diagnostic application designed to let you see "what is running on this PC or server" at a single glance.  
It visualizes key services categorized clearly and inspects basic local security configurations—including Firewalls, listening ports, and bind addresses—in a completely safe and non-intrusive way.

> A local-first, read-only service discovery and port exposure diagnostic tool for Windows and Linux (GUI & headless CLI `esd`)—100% offline with Markdown/HTML export and local Wi-Fi QR transfer.

> **Note:** This is a diagnostic inspection tool, not a management console to start/stop services, nor an intrusive vulnerability scanner targeting external machines.

---

## 📥 Download

- **🪟 Windows 10 / 11 (GUI)**: [Get it free from the Microsoft Store](https://apps.microsoft.com/detail/9mxt54r13d17?hl=en&gl=US&ocid=pdpshare)
- **🐧 Linux (Flatpak bundle / CLI `esd`)**: Available via GitHub Releases (or build from source)

---

## 📸 Screenshots

| Main Screen (Categories, Service List, Security Score) | Report Export & Local Wi-Fi QR Code Transfer | One-time QR (Invalidated after transfer) |
| :---: | :---: | :---: |
| <img width="440" alt="easy_service_check_top" src="https://github.com/user-attachments/assets/2c1d0121-ff74-40ac-9235-f5c692fd3491" /> | <img width="440" alt="easy_service_check_qr_transfer" src="https://github.com/user-attachments/assets/e7a3575a-7f31-467b-9586-23245f6e0b3d" /> | <img width="440" alt="easy_service_check_qr_transfer2" src="https://github.com/user-attachments/assets/cf435059-f94e-418f-8172-b718cdc5f1f4" /> |

| Smartphone Receive Screen | HTML Report Output |
| :---: | :---: |
| <img width="230" alt="easy_service_receive" src="https://github.com/user-attachments/assets/8eeda4b5-22ea-4845-be58-35b264025c46" /> | <img width="230" alt="easy_service_receive_2" src="https://github.com/user-attachments/assets/4f8e1d45-490d-44fc-9412-b332ccbc9012" /> |

---

## Key Features

- **Automatic Service Discovery**: Detects OS-level services and displays category, status, and listening ports in real time.
- **Fast Search & Filtering**: Instant search by service name, display name, and category. Filter by 12 categories or your Favorites list.
- **Service Details & One-Click URL Launch**: Inspect detailed service info; open web dashboards directly in your default browser for services listening on HTTP/HTTPS.
- **Custom Service Registration**: Manually add custom services or microservices to track.
- **Local Security Diagnostics (100-Point Score)**: Evaluates Firewall status, high-risk exposed ports, SSH settings, and HTTPS/HTTP exposure.
- **Container Inspection (Linux)**: Checks local Docker and Podman container statuses, privileged mode flags, and exposed ports.
- **Report Export & QR Code Transfer**: Export diagnostic reports as Markdown or HTML; securely transfer reports to your smartphone over the local Wi-Fi network with a one-time QR code.
- **Headless CLI (`esd`)**: Single-file binary sharing the identical diagnostic engine for server environments and automated cron monitoring.
- **12 Languages Supported**: English, Japanese, Simplified Chinese, Traditional Chinese, Korean, Vietnamese, German, French, Spanish, Italian, Portuguese, and Russian.

The language can be changed from the top of the diagnostic results panel and is saved to a configuration file:
- **Windows**: `%APPDATA%\EasyServiceManager\settings.json`
- **Linux**: `~/.config/EasyServiceManager/settings.json`

---

## Service Discovery

### Supported Operating Systems

- Windows 10 / 11
- Linux distributions with systemd

Works out of the box without distro-specific configuration. On Linux, standard `systemctl` and `ss` commands are used.

### Detection Methods

| OS | Services | Listening Ports |
| --- | --- | --- |
| **Windows** | Windows Service API (via PowerShell) | TCP listening sockets and process info (via PowerShell) |
| **Linux** | `systemctl list-units --type=service --all` | `ss -lntp` |

On Linux, loaded systemd services are enumerated (it does not list un-loaded unit files). Listening ports are correlated with services via process names.

### Categories

- Web Server
- Container
- Database
- SSH
- Monitoring
- Mail
- File Share
- Virtualization
- AI / LLM
- Cloud Agent
- Security
- Other

Services are automatically classified based on internal rule sets. Any unrecognized service is placed under **Other**.

### Status Indicators

- 🟢 **Running**
- 🔴 **Stopped**
- ⚪ **Unknown**

### URL Launching

Services in the Web Server, Monitoring, and AI / LLM categories that have an active listening port are assigned a `localhost` URL (HTTPS if port is 443, otherwise HTTP). This is inferred from the port number and does not validate the underlying web application or TLS certificate.

Custom services can also have HTTP or HTTPS URLs registered, allowing you to open them in your default browser from the details view.

---

## Security Diagnostics

Diagnostics are strictly read-only: the application reads current system configuration state and never makes modifications or runs active network scans against external hosts.

### Check Items

| Item | Linux | Windows |
| --- | --- | --- |
| **Firewall** | UFW / firewalld status | Windows Firewall active profiles |
| **High-Risk Ports** | TCP listening check via `ss` | Windows TCP listening sockets |
| **SSH Configuration** | Effective config via `sshd -T` | Not applicable |
| **HTTPS Ports** | Listening on TCP/80 & TCP/443 | Listening on TCP/80 & TCP/443 |
| **Docker / Podman** | Running containers, privileged mode, port bindings | Not applicable |

**High-risk ports monitored:** `3306` (MySQL), `5432` (PostgreSQL), `6379` (Redis), `9200` (Elasticsearch), `5601` (Kibana), `27017` (MongoDB), `5000` (Docker Registry / Dev servers).  
A warning is triggered when these ports are bound to all network interfaces (`0.0.0.0` or `::`). Actual external reachability depends on network boundaries such as Firewalls, NAT, or router port forwarding.

Docker is checked via the local Unix socket, and Podman in rootless/local mode. Remote Docker contexts or remote Podman daemons are not queried. If connection to the container runtime fails, it is shown as "Unknown".

### Scoring System

Deductions are made from a base score of 100 points based on diagnostic findings:

| Grade | Score |
| --- | --- |
| **A** | 95 – 100 |
| **B** | 80 – 94 |
| **C** | 60 – 79 |
| **D** | 0 – 59 |

Items that could not be verified are flagged as "Provisional" rather than assumed safe. These checks represent basic configuration hygiene and do not guarantee the absence of software vulnerabilities.

---

## 💻 CLI Version (`esd`) — Headless Server & Cron Monitoring

For headless Linux servers without a graphical interface, the single-file CLI (`esd`) provides the exact same diagnostic engine and scoring standards.

```bash
# Instant scan
esd

# Scan and save a snapshot for future comparison
esd scan --save-snapshot

# Check diff against the previous snapshot (newly exposed ports, bind changes, etc.)
esd diff

# Filter running services and export as an HTML report
esd scan --only-running --format html --output report.html

# For cron / automated monitoring: quiet summary with exit codes (0–3) for alert triggers
esd scan --quiet --format text
```

### Exit Codes (for cron / alert hooks)

| Exit Code | Meaning |
| :---: | --- |
| `0` | No warnings (Clean) |
| `1` | Warnings found (Caution level) |
| `2` | Danger level found (e.g. database port bound to `0.0.0.0`) |
| `3` | Diagnostic execution failed |

### Main Options

| Option | Description |
| --- | --- |
| `--format text\|json\|markdown\|html` | Output format (default: `text`) |
| `--output <path>` | Write output to a file (stdout if omitted) |
| `--only-running` | Include only active/running services in the report |
| `--category <name>` | Filter by category (same names as in the GUI) |
| `--save-snapshot [path]` | Save the scan result as a snapshot |
| `--diff` | Display diff against previous snapshot when saving |
| `--no-color` | Disable colored terminal output (auto-disabled when piping) |
| `--quiet` | Output only the score and warning summary |

```bash
esd version              # Print version info
esd help                 # Show command help
esd diff                 # Show only diff against the previous snapshot
```

Snapshot file path: `~/.local/share/easy-service-diagnosis/snapshots/snapshot.json`

### Getting Started (Linux CLI)

Download and extract the `tar.gz` archive from [GitHub Releases](https://github.com/kumatarosasaki3891-art/EasyServiceManager/releases), make it executable, and run it:

```bash
tar xzf esd-linux-x64.tar.gz
chmod +x esd
./esd scan --quiet --format text; echo $?
```
As a self-contained single-file binary, no .NET runtime installation is required.

### Verification & Consistency

The CLI (`esd`) is verified to produce the exact same diagnostic score and warnings as the GUI for the identical system state (e.g. 75/100, Grade C, exit code 2).

