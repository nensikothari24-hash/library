# Stacks — Library Management System

A librarian-facing library management system built with ASP.NET Core 8, Entity Framework Core, SQLite, and a responsive browser frontend.

## Features

- Librarian sign-in using ASP.NET Core Identity cookies
- A single provisioned librarian account; public registration is disabled
- Dashboard with collection, member, loan, overdue, and reservation summaries
- Book catalog with search, categories, inventory counts, and editing
- Member records with contact details and active/inactive status
- Circulation: checkout, return, two renewals per loan, and a five-loan member limit
- Reservations and cancellation
- Overdue fines, calculated at $0.50 per day on return, and payment recording
- Reports for checkouts, active loans, overdue books, fines, and most-borrowed titles
- SQLite persistence in `library.db`
- Sample catalog, members, loans, reservations, and fines for a fresh database

## Run locally

Install the .NET 8 SDK, then from this directory run:

```powershell
dotnet restore
dotnet run
```

Open the `http://localhost:5080` URL printed by ASP.NET Core. Do not open `wwwroot/index.html` directly as a file: the frontend needs the ASP.NET server for its sign-in and API requests. The provisioned librarian account is:

- Email: configured with `LIBRARY_ADMIN_EMAIL` (defaults to `nen@gmail.com`)
- Password: set securely with `LIBRARY_ADMIN_PASSWORD`

For local development, store the requested credentials with .NET User Secrets so the password is not committed to source control:

```powershell
dotnet user-secrets set "LIBRARY_ADMIN_EMAIL" "nen@gmail.com"
dotnet user-secrets set "LIBRARY_ADMIN_PASSWORD" "<your-librarian-password>"
dotnet run
```

The project already has a User Secrets ID configured. On startup, the app provisions the configured librarian and synchronizes its password. All other existing Identity accounts are locked out and removed from the librarian role. There is no public registration endpoint or registration form. The requested password is short and predictable: use it only for local/demo work on localhost, and replace it with a strong unique password before exposing the app to a network.

The database is created on first startup and the initial librarian account, standard categories, and sample module records are seeded automatically. Sample records are added only when all library-data tables are empty; existing records are left unchanged. Back up `library.db` regularly. The starter currently uses EF Core's `EnsureCreated`; schema evolution for a deployed database should be handled with EF Core migrations.

## Notes

- The app enforces a maximum of five active loans per member, a standard 14-day term, and up to two renewals.
- Reservations are queued per title; when a copy is returned, the next waiting request is marked ready for pickup. Completing that reservation creates the checkout.
- Members with circulation history are deactivated rather than deleted, preserving historical records. Books with any circulation/reservation history cannot be deleted.
- Fine currency is currently USD and late charges are created when a late book is returned.
- This starter is intended to run behind HTTPS. Configure the reverse proxy and secure `LIBRARY_ADMIN_PASSWORD` for deployment.
