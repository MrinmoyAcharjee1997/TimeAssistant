# TimeTracker

A native Windows personal time tracker and decision assistant.

## Development state

This repository currently contains only the agreed project structure and storage contract. Application features are added incrementally after the structure is approved.

## Runtime data

During development, the application data lives in `database/TimeTracker.md`. This is a single, user-readable Markdown file. The eventual Settings screen will let a user move it to a chosen location.

## Deployment approach

The app is developed as an unpackaged, self-contained WinUI 3 application. MSIX packaging is deferred until release preparation.
