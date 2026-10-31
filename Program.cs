using System.Security.Claims;
using System.Text.Json.Serialization;
using LibraryManagement;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<LibraryDb>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Library") ?? "Data Source=library.db"));
builder.Services
    .AddIdentityCore<LibrarianUser>(options =>
    {
        options.Password.RequiredLength = 6;
        options.Password.RequiredUniqueChars = 1;
        options.Password.RequireDigit = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<LibraryDb>()
    .AddSignInManager();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();
builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options =>
{
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LibraryDb>();
    await db.Database.EnsureCreatedAsync();
    await SeedLibrarianAsync(scope.ServiceProvider);
    await SeedDemoLibraryDataAsync(db);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var auth = app.MapGroup("/api/auth");
auth.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
{
    authenticated = user.Identity?.IsAuthenticated == true,
    name = user.Identity?.Name
})).AllowAnonymous();
auth.MapPost("/login", async (LoginRequest request, SignInManager<LibrarianUser> signIn) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { message = "Email and password are required." });
    var result = await signIn.PasswordSignInAsync(request.Email, request.Password, request.RememberMe, lockoutOnFailure: true);
    return result.Succeeded
        ? Results.Ok(new { message = "Signed in." })
        : Results.Unauthorized();
}).AllowAnonymous();
auth.MapPost("/logout", async (SignInManager<LibrarianUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.Ok(new { message = "Signed out." });
}).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization(policy => policy.RequireRole("Librarian"));

api.MapGet("/dashboard", async (LibraryDb db) =>
{
    var today = DateTime.UtcNow.Date;
    var unpaidFines = await db.Fines.Where(fine => !fine.IsPaid)
        .Select(fine => fine.Amount).ToListAsync();
    return Results.Ok(new
    {
        totalBooks = await db.Books.SumAsync(book => book.TotalCopies),
        availableCopies = await db.Books.SumAsync(book => book.AvailableCopies),
        activeMembers = await db.Members.CountAsync(member => member.IsActive),
        booksOnLoan = await db.Loans.CountAsync(loan => loan.ReturnedAt == null),
        overdueLoans = await db.Loans.CountAsync(loan => loan.ReturnedAt == null && loan.DueDate < today),
        pendingReservations = await db.Reservations.CountAsync(reservation =>
            reservation.Status == ReservationStatus.Pending || reservation.Status == ReservationStatus.ReadyForPickup),
        unpaidFines = unpaidFines.Sum(),
        recentLoans = await db.Loans.Include(loan => loan.Book).Include(loan => loan.Member)
            .OrderByDescending(loan => loan.CheckedOutAt).Take(6).ToListAsync()
    });
});

api.MapGet("/books", async (LibraryDb db, string? search, int? categoryId) =>
{
    var query = db.Books.AsQueryable();
    if (!string.IsNullOrWhiteSpace(search))
        query = query.Where(book => book.Title.Contains(search) || book.Author.Contains(search) || book.Isbn.Contains(search));
    if (categoryId.HasValue)
        query = query.Where(book => book.CategoryId == categoryId);
    return Results.Ok(await query.OrderBy(book => book.Title).ToListAsync());
});
api.MapPost("/books", async (BookInput input, LibraryDb db) =>
{
    var error = ValidateBook(input);
    if (error is not null) return Results.BadRequest(new { message = error });
    var isbn = input.Isbn.Trim();
    if (await db.Books.AnyAsync(book => book.Isbn == isbn))
        return Results.Conflict(new { message = "A book with this ISBN already exists." });
    if (input.CategoryId is int categoryId && !await db.Categories.AnyAsync(category => category.Id == categoryId))
        return Results.BadRequest(new { message = "Select a valid category." });
    var book = new Book
    {
        Title = input.Title.Trim(), Author = input.Author.Trim(), Isbn = isbn,
        Publisher = input.Publisher?.Trim() ?? "", CategoryId = input.CategoryId,
        PublishedYear = input.PublishedYear, TotalCopies = input.TotalCopies,
        AvailableCopies = input.TotalCopies, Description = input.Description?.Trim() ?? ""
    };
    db.Books.Add(book);
    await db.SaveChangesAsync();
    return Results.Created($"/api/books/{book.Id}", book);
});
api.MapPut("/books/{id:int}", async (int id, BookInput input, LibraryDb db) =>
{
    var error = ValidateBook(input);
    if (error is not null) return Results.BadRequest(new { message = error });
    var book = await db.Books.FindAsync(id);
    if (book is null) return Results.NotFound();
    var isbn = input.Isbn.Trim();
    if (await db.Books.AnyAsync(existing => existing.Id != id && existing.Isbn == isbn))
        return Results.Conflict(new { message = "A book with this ISBN already exists." });
    if (input.CategoryId is int categoryId && !await db.Categories.AnyAsync(category => category.Id == categoryId))
        return Results.BadRequest(new { message = "Select a valid category." });
    var borrowed = book.TotalCopies - book.AvailableCopies;
    if (input.TotalCopies < borrowed)
        return Results.BadRequest(new { message = $"At least {borrowed} copies are currently on loan." });
    book.Title = input.Title.Trim();
    book.Author = input.Author.Trim();
    book.Isbn = isbn;
    book.Publisher = input.Publisher?.Trim() ?? "";
    book.CategoryId = input.CategoryId;
    book.PublishedYear = input.PublishedYear;
    book.TotalCopies = input.TotalCopies;
    book.AvailableCopies = input.TotalCopies - borrowed;
    book.Description = input.Description?.Trim() ?? "";
    await db.SaveChangesAsync();
    return Results.Ok(book);
});
api.MapDelete("/books/{id:int}", async (int id, LibraryDb db) =>
{
    var book = await db.Books.FindAsync(id);
    if (book is null) return Results.NotFound();
    if (await db.Loans.AnyAsync(loan => loan.BookId == id) ||
        await db.Reservations.AnyAsync(reservation => reservation.BookId == id))
        return Results.Conflict(new { message = "Books with circulation or reservation history cannot be removed." });
    db.Books.Remove(book);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

api.MapGet("/categories", async (LibraryDb db) => Results.Ok(await db.Categories.OrderBy(category => category.Name).ToListAsync()));
api.MapPost("/categories", async (CategoryInput input, LibraryDb db) =>
{
    if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { message = "Category name is required." });
    var name = input.Name.Trim();
    if (await db.Categories.AnyAsync(category => category.Name == name))
        return Results.Conflict(new { message = "That category already exists." });
    var category = new Category { Name = name };
    db.Categories.Add(category);
    await db.SaveChangesAsync();
    return Results.Created($"/api/categories/{category.Id}", category);
});

api.MapGet("/members", async (LibraryDb db, string? search) =>
{
    var query = db.Members.AsQueryable();
    if (!string.IsNullOrWhiteSpace(search))
        query = query.Where(member => member.FullName.Contains(search) || member.Email.Contains(search) || member.MemberNumber.Contains(search));
    return Results.Ok(await query.OrderBy(member => member.FullName).ToListAsync());
});
api.MapPost("/members", async (MemberInput input, LibraryDb db) =>
{
    var error = ValidateMember(input);
    if (error is not null) return Results.BadRequest(new { message = error });
    var email = input.Email.Trim().ToLowerInvariant();
    var memberNumber = input.MemberNumber.Trim();
    if (await db.Members.AnyAsync(member => member.Email == email || member.MemberNumber == memberNumber))
        return Results.Conflict(new { message = "Email and member number must be unique." });
    var member = new Member
    {
        FullName = input.FullName.Trim(), Email = email, Phone = input.Phone.Trim(),
        MemberNumber = memberNumber, Address = input.Address?.Trim() ?? "",
        JoinedAt = DateTime.UtcNow, IsActive = true
    };
    db.Members.Add(member);
    await db.SaveChangesAsync();
    return Results.Created($"/api/members/{member.Id}", member);
});
api.MapPut("/members/{id:int}", async (int id, MemberInput input, LibraryDb db) =>
{
    var error = ValidateMember(input);
    if (error is not null) return Results.BadRequest(new { message = error });
    var member = await db.Members.FindAsync(id);
    if (member is null) return Results.NotFound();
    var email = input.Email.Trim().ToLowerInvariant();
    var memberNumber = input.MemberNumber.Trim();
    if (await db.Members.AnyAsync(existing => existing.Id != id &&
        (existing.Email == email || existing.MemberNumber == memberNumber)))
        return Results.Conflict(new { message = "Email and member number must be unique." });
    member.FullName = input.FullName.Trim();
    member.Email = email;
    member.Phone = input.Phone.Trim();
    member.MemberNumber = memberNumber;
    member.Address = input.Address?.Trim() ?? "";
    member.IsActive = input.IsActive;
    await db.SaveChangesAsync();
    return Results.Ok(member);
});
api.MapDelete("/members/{id:int}", async (int id, LibraryDb db) =>
{
    var member = await db.Members.FindAsync(id);
    if (member is null) return Results.NotFound();
    if (await db.Loans.AnyAsync(loan => loan.MemberId == id && loan.ReturnedAt == null))
        return Results.Conflict(new { message = "This member has active loans. Return them before removing the member." });
    member.IsActive = false;
    await db.SaveChangesAsync();
    return Results.Ok(member);
});

api.MapGet("/loans", async (LibraryDb db, bool? overdue) =>
{
    var today = DateTime.UtcNow.Date;
    var query = db.Loans.Include(loan => loan.Book).Include(loan => loan.Member).AsQueryable();
    if (overdue == true) query = query.Where(loan => loan.ReturnedAt == null && loan.DueDate < today);
    return Results.Ok(await query.OrderByDescending(loan => loan.CheckedOutAt).ToListAsync());
});
api.MapPost("/loans/checkout", async (CheckoutInput input, LibraryDb db) =>
{
    var book = await db.Books.FindAsync(input.BookId);
    var member = await db.Members.FindAsync(input.MemberId);
    if (book is null) return Results.NotFound(new { message = "Book not found." });
    if (member is null) return Results.NotFound(new { message = "Member not found." });
    if (!member.IsActive) return Results.BadRequest(new { message = "This member account is inactive." });
    if (book.AvailableCopies <= 0) return Results.Conflict(new { message = "No copies are currently available." });
    if (await db.Loans.CountAsync(loan => loan.MemberId == member.Id && loan.ReturnedAt == null) >= 5)
        return Results.Conflict(new { message = "A member may have up to 5 active loans." });
    var pendingReservation = await db.Reservations
        .Where(reservation => reservation.BookId == book.Id &&
            (reservation.Status == ReservationStatus.Pending || reservation.Status == ReservationStatus.ReadyForPickup))
        .OrderBy(reservation => reservation.CreatedAt)
        .FirstOrDefaultAsync();
    if (input.ReservationId is int reservationId)
    {
        if (pendingReservation is null || pendingReservation.Id != reservationId ||
            pendingReservation.MemberId != member.Id || pendingReservation.Status != ReservationStatus.ReadyForPickup)
            return Results.Conflict(new { message = "This reservation is not ready for checkout." });
        pendingReservation.Status = ReservationStatus.Fulfilled;
    }
    else if (pendingReservation is not null)
    {
        return Results.Conflict(new { message = "This book is reserved. Check it out using the next reservation in the queue." });
    }
    var loan = new Loan
    {
        BookId = book.Id, MemberId = member.Id, CheckedOutAt = DateTime.UtcNow,
        DueDate = DateTime.UtcNow.Date.AddDays(14), RenewalCount = 0
    };
    book.AvailableCopies--;
    db.Loans.Add(loan);
    await db.SaveChangesAsync();
    return Results.Created($"/api/loans/{loan.Id}", loan);
});
api.MapPost("/loans/{id:int}/return", async (int id, LibraryDb db) =>
{
    var loan = await db.Loans.Include(item => item.Book).FirstOrDefaultAsync(item => item.Id == id);
    if (loan is null) return Results.NotFound();
    if (loan.ReturnedAt is not null) return Results.Conflict(new { message = "This loan has already been returned." });
    loan.ReturnedAt = DateTime.UtcNow;
    loan.Book.AvailableCopies++;
    var overdueDays = Math.Max(0, (DateTime.UtcNow.Date - loan.DueDate.Date).Days);
    Fine? fine = null;
    if (overdueDays > 0)
    {
        fine = new Fine
        {
            MemberId = loan.MemberId, LoanId = loan.Id, Amount = overdueDays * 0.50m,
            Reason = $"Returned {overdueDays} day(s) late", IssuedAt = DateTime.UtcNow
        };
        db.Fines.Add(fine);
    }
    var nextReservation = await db.Reservations
        .Where(reservation => reservation.BookId == loan.BookId && reservation.Status == ReservationStatus.Pending)
        .OrderBy(reservation => reservation.CreatedAt).FirstOrDefaultAsync();
    if (nextReservation is not null) nextReservation.Status = ReservationStatus.ReadyForPickup;
    await db.SaveChangesAsync();
    return Results.Ok(new { loan, fine });
});
api.MapPost("/loans/{id:int}/renew", async (int id, LibraryDb db) =>
{
    var loan = await db.Loans.Include(item => item.Book).FirstOrDefaultAsync(item => item.Id == id);
    if (loan is null) return Results.NotFound();
    if (loan.ReturnedAt is not null) return Results.Conflict(new { message = "Returned loans cannot be renewed." });
    if (loan.RenewalCount >= 2) return Results.Conflict(new { message = "This loan has reached its renewal limit." });
    if (await db.Reservations.AnyAsync(reservation => reservation.BookId == loan.BookId &&
        (reservation.Status == ReservationStatus.Pending || reservation.Status == ReservationStatus.ReadyForPickup)))
        return Results.Conflict(new { message = "This book is reserved by another member and cannot be renewed." });
    loan.DueDate = loan.DueDate.Date.AddDays(14);
    loan.RenewalCount++;
    await db.SaveChangesAsync();
    return Results.Ok(loan);
});

api.MapGet("/reservations", async (LibraryDb db) =>
    Results.Ok(await db.Reservations.Include(item => item.Book).Include(item => item.Member)
        .OrderBy(item => item.Status).ThenBy(item => item.CreatedAt).ToListAsync()));
api.MapPost("/reservations", async (ReservationInput input, LibraryDb db) =>
{
    var book = await db.Books.FindAsync(input.BookId);
    var member = await db.Members.FindAsync(input.MemberId);
    if (book is null || member is null) return Results.NotFound(new { message = "Select a valid book and member." });
    if (!member.IsActive) return Results.BadRequest(new { message = "This member account is inactive." });
    if (await db.Reservations.AnyAsync(item => item.BookId == input.BookId && item.MemberId == input.MemberId &&
        (item.Status == ReservationStatus.Pending || item.Status == ReservationStatus.ReadyForPickup)))
        return Results.Conflict(new { message = "This member already has a pending reservation for this book." });
    var readyReservations = await db.Reservations.CountAsync(item =>
        item.BookId == book.Id && item.Status == ReservationStatus.ReadyForPickup);
    var reservation = new Reservation
    {
        BookId = book.Id, MemberId = member.Id, CreatedAt = DateTime.UtcNow,
        Status = book.AvailableCopies > readyReservations
            ? ReservationStatus.ReadyForPickup
            : ReservationStatus.Pending
    };
    db.Reservations.Add(reservation);
    await db.SaveChangesAsync();
    return Results.Created($"/api/reservations/{reservation.Id}", reservation);
});
api.MapPost("/reservations/{id:int}/cancel", async (int id, LibraryDb db) =>
{
    var reservation = await db.Reservations.FindAsync(id);
    if (reservation is null) return Results.NotFound();
    if (reservation.Status is not (ReservationStatus.Pending or ReservationStatus.ReadyForPickup))
        return Results.Conflict(new { message = "Only open reservations can be cancelled." });
    var wasReady = reservation.Status == ReservationStatus.ReadyForPickup;
    reservation.Status = ReservationStatus.Cancelled;
    if (wasReady)
    {
        var book = await db.Books.FindAsync(reservation.BookId);
        var readyCount = await db.Reservations.CountAsync(item =>
            item.BookId == reservation.BookId && item.Status == ReservationStatus.ReadyForPickup);
        if (book is not null && book.AvailableCopies > readyCount)
        {
            var next = await db.Reservations.Where(item =>
                    item.BookId == reservation.BookId && item.Status == ReservationStatus.Pending)
                .OrderBy(item => item.CreatedAt).FirstOrDefaultAsync();
            if (next is not null) next.Status = ReservationStatus.ReadyForPickup;
        }
    }
    await db.SaveChangesAsync();
    return Results.Ok(reservation);
});

api.MapGet("/fines", async (LibraryDb db, bool? unpaid) =>
{
    var query = db.Fines.Include(fine => fine.Member).Include(fine => fine.Loan).ThenInclude(loan => loan.Book).AsQueryable();
    if (unpaid == true) query = query.Where(fine => !fine.IsPaid);
    return Results.Ok(await query.OrderByDescending(fine => fine.IssuedAt).ToListAsync());
});
api.MapPost("/fines/{id:int}/pay", async (int id, LibraryDb db) =>
{
    var fine = await db.Fines.FindAsync(id);
    if (fine is null) return Results.NotFound();
    if (fine.IsPaid) return Results.Conflict(new { message = "This fine has already been paid." });
    fine.IsPaid = true;
    fine.PaidAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(fine);
});

api.MapGet("/reports", async (LibraryDb db) =>
{
    var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    var unpaidFineAmounts = await db.Fines.Where(fine => !fine.IsPaid)
        .Select(fine => fine.Amount).ToListAsync();
    return Results.Ok(new
    {
        mostBorrowedBooks = await db.Loans.GroupBy(loan => new { loan.BookId, loan.Book.Title, loan.Book.Author })
            .Select(group => new { group.Key.Title, group.Key.Author, Checkouts = group.Count() })
            .OrderByDescending(book => book.Checkouts).Take(10).ToListAsync(),
        monthlyCheckouts = await db.Loans.CountAsync(loan => loan.CheckedOutAt >= monthStart),
        overdueCount = await db.Loans.CountAsync(loan => loan.ReturnedAt == null && loan.DueDate < DateTime.UtcNow.Date),
        unpaidFineTotal = unpaidFineAmounts.Sum(),
        memberCount = await db.Members.CountAsync(),
        activeLoanCount = await db.Loans.CountAsync(loan => loan.ReturnedAt == null)
    });
});

app.MapFallbackToFile("index.html");
app.Run();

static string? ValidateBook(BookInput input)
{
    if (string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.Author) || string.IsNullOrWhiteSpace(input.Isbn))
        return "Title, author, and ISBN are required.";
    if (input.TotalCopies < 1) return "At least one copy is required.";
    if (input.PublishedYear is < 1000 or > 2100) return "Enter a valid publication year.";
    return null;
}

static string? ValidateMember(MemberInput input)
{
    if (string.IsNullOrWhiteSpace(input.FullName) || string.IsNullOrWhiteSpace(input.Email) ||
        string.IsNullOrWhiteSpace(input.Phone) || string.IsNullOrWhiteSpace(input.MemberNumber))
        return "Name, email, phone, and member number are required.";
    if (!input.Email.Contains('@')) return "Enter a valid email address.";
    return null;
}

static async Task SeedLibrarianAsync(IServiceProvider services)
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var email = configuration["LIBRARY_ADMIN_EMAIL"] ?? "nen@gmail.com";
    var password = configuration["LIBRARY_ADMIN_PASSWORD"];
    if (string.IsNullOrWhiteSpace(password))
        throw new InvalidOperationException("Configure LIBRARY_ADMIN_PASSWORD using .NET User Secrets or a secure environment variable.");

    var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync("Librarian"))
    {
        var roleResult = await roles.CreateAsync(new IdentityRole("Librarian"));
        if (!roleResult.Succeeded) throw new InvalidOperationException(string.Join("; ", roleResult.Errors.Select(error => error.Description)));
    }
    var users = services.GetRequiredService<UserManager<LibrarianUser>>();
    var user = await users.FindByEmailAsync(email);
    if (user is null)
    {
        user = new LibrarianUser { UserName = email, Email = email, EmailConfirmed = true };
        var create = await users.CreateAsync(user, password);
        if (!create.Succeeded) throw new InvalidOperationException(string.Join("; ", create.Errors.Select(error => error.Description)));
    }
    else
    {
        user.Email = email;
        user.UserName = email;
        user.EmailConfirmed = true;
        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
        var update = await users.UpdateAsync(user);
        if (!update.Succeeded) throw new InvalidOperationException(string.Join("; ", update.Errors.Select(error => error.Description)));
        var refreshSessions = await users.UpdateSecurityStampAsync(user);
        if (!refreshSessions.Succeeded) throw new InvalidOperationException(string.Join("; ", refreshSessions.Errors.Select(error => error.Description)));
    }
    var enableSignIn = await users.SetLockoutEnabledAsync(user, false);
    if (!enableSignIn.Succeeded) throw new InvalidOperationException(string.Join("; ", enableSignIn.Errors.Select(error => error.Description)));
    if (await users.IsLockedOutAsync(user))
    {
        var unlock = await users.SetLockoutEndDateAsync(user, null);
        if (!unlock.Succeeded) throw new InvalidOperationException(string.Join("; ", unlock.Errors.Select(error => error.Description)));
    }

    var assign = await users.AddToRoleAsync(user, "Librarian");
    if (!assign.Succeeded && !await users.IsInRoleAsync(user, "Librarian"))
        throw new InvalidOperationException(string.Join("; ", assign.Errors.Select(error => error.Description)));

    var otherUsers = await users.Users.Where(candidate => candidate.Id != user.Id).ToListAsync();
    foreach (var otherUser in otherUsers)
    {
        if (await users.IsInRoleAsync(otherUser, "Librarian"))
        {
            var removeRole = await users.RemoveFromRoleAsync(otherUser, "Librarian");
            if (!removeRole.Succeeded) throw new InvalidOperationException(string.Join("; ", removeRole.Errors.Select(error => error.Description)));
        }
        var lockoutEnabled = await users.SetLockoutEnabledAsync(otherUser, true);
        if (!lockoutEnabled.Succeeded) throw new InvalidOperationException(string.Join("; ", lockoutEnabled.Errors.Select(error => error.Description)));
        var lockout = await users.SetLockoutEndDateAsync(otherUser, DateTimeOffset.MaxValue);
        if (!lockout.Succeeded) throw new InvalidOperationException(string.Join("; ", lockout.Errors.Select(error => error.Description)));
    }

    var db = services.GetRequiredService<LibraryDb>();
    if (!await db.Categories.AnyAsync())
    {
        db.Categories.AddRange(
            new Category { Name = "Fiction" }, new Category { Name = "Non-fiction" },
            new Category { Name = "Science" }, new Category { Name = "History" },
            new Category { Name = "Children" }, new Category { Name = "Reference" });
        await db.SaveChangesAsync();
    }
}

static async Task SeedDemoLibraryDataAsync(LibraryDb db)
{
    if (await db.Books.AnyAsync() || await db.Members.AnyAsync() ||
        await db.Loans.AnyAsync() || await db.Reservations.AnyAsync() || await db.Fines.AnyAsync())
        return;

    await using var transaction = await db.Database.BeginTransactionAsync();
    var categories = await db.Categories.ToDictionaryAsync(category => category.Name);
    var fiction = categories["Fiction"].Id;
    var nonFiction = categories["Non-fiction"].Id;
    var science = categories["Science"].Id;
    var history = categories["History"].Id;
    var children = categories["Children"].Id;
    var reference = categories["Reference"].Id;

    var books = new List<Book>
    {
        new() { Title = "The Midnight Library", Author = "Matt Haig", Isbn = "9780525559474", Publisher = "Viking", CategoryId = fiction, PublishedYear = 2020, TotalCopies = 3, AvailableCopies = 2, Description = "A novel about choices, possibility, and second chances." },
        new() { Title = "Educated", Author = "Tara Westover", Isbn = "9780399590504", Publisher = "Random House", CategoryId = nonFiction, PublishedYear = 2018, TotalCopies = 3, AvailableCopies = 2, Description = "A memoir about education and finding your own path." },
        new() { Title = "A Short History of Nearly Everything", Author = "Bill Bryson", Isbn = "9780767908184", Publisher = "Broadway Books", CategoryId = science, PublishedYear = 2003, TotalCopies = 2, AvailableCopies = 2, Description = "An accessible tour of scientific discoveries." },
        new() { Title = "The Silk Roads", Author = "Peter Frankopan", Isbn = "9781101912379", Publisher = "Vintage", CategoryId = history, PublishedYear = 2015, TotalCopies = 1, AvailableCopies = 0, Description = "A history of the connections between East and West." },
        new() { Title = "Charlotte's Web", Author = "E. B. White", Isbn = "9780064400558", Publisher = "HarperCollins", CategoryId = children, PublishedYear = 1952, TotalCopies = 4, AvailableCopies = 4, Description = "A children's classic about friendship and kindness." },
        new() { Title = "The Art of Clear Thinking", Author = "Library Reference Desk", Isbn = "9781639381234", Publisher = "Northfield Press", CategoryId = reference, PublishedYear = 2022, TotalCopies = 2, AvailableCopies = 2, Description = "A sample reference-desk title for the demo catalog." }
    };

    var members = new List<Member>
    {
        new() { FullName = "Avery Morgan", Email = "avery.morgan@example.com", Phone = "555-0101", MemberNumber = "MEM-1001", Address = "12 Maple Street", JoinedAt = DateTime.UtcNow.AddMonths(-10), IsActive = true },
        new() { FullName = "Jordan Lee", Email = "jordan.lee@example.com", Phone = "555-0102", MemberNumber = "MEM-1002", Address = "84 Oak Avenue", JoinedAt = DateTime.UtcNow.AddMonths(-7), IsActive = true },
        new() { FullName = "Samira Patel", Email = "samira.patel@example.com", Phone = "555-0103", MemberNumber = "MEM-1003", Address = "3 Garden Lane", JoinedAt = DateTime.UtcNow.AddMonths(-5), IsActive = true },
        new() { FullName = "Casey Rivera", Email = "casey.rivera@example.com", Phone = "555-0104", MemberNumber = "MEM-1004", Address = "27 Cedar Road", JoinedAt = DateTime.UtcNow.AddMonths(-3), IsActive = true },
        new() { FullName = "Taylor Brooks", Email = "taylor.brooks@example.com", Phone = "555-0105", MemberNumber = "MEM-1005", Address = "51 Pine Court", JoinedAt = DateTime.UtcNow.AddMonths(-1), IsActive = true }
    };

    db.Books.AddRange(books);
    db.Members.AddRange(members);
    await db.SaveChangesAsync();

    var today = DateTime.UtcNow.Date;
    var lateReturnedLoan = new Loan
    {
        Book = books[0], Member = members[1], CheckedOutAt = today.AddDays(-28),
        DueDate = today.AddDays(-14), ReturnedAt = today.AddDays(-8), RenewalCount = 0
    };
    var onTimeReturnedLoan = new Loan
    {
        Book = books[2], Member = members[2], CheckedOutAt = today.AddDays(-20),
        DueDate = today.AddDays(-10), ReturnedAt = today.AddDays(-7), RenewalCount = 0
    };
    var loans = new List<Loan>
    {
        new() { Book = books[0], Member = members[0], CheckedOutAt = today.AddDays(-23), DueDate = today.AddDays(-9), RenewalCount = 0 },
        new() { Book = books[1], Member = members[1], CheckedOutAt = today.AddDays(-2), DueDate = today.AddDays(12), RenewalCount = 0 },
        new() { Book = books[3], Member = members[3], CheckedOutAt = today.AddDays(-4), DueDate = today.AddDays(10), RenewalCount = 1 },
        lateReturnedLoan,
        onTimeReturnedLoan
    };
    db.Loans.AddRange(loans);
    await db.SaveChangesAsync();

    db.Reservations.AddRange(
        new Reservation { Book = books[4], Member = members[4], CreatedAt = DateTime.UtcNow.AddDays(-1), Status = ReservationStatus.ReadyForPickup },
        new Reservation { Book = books[3], Member = members[2], CreatedAt = DateTime.UtcNow, Status = ReservationStatus.Pending });
    db.Fines.AddRange(
        new Fine
        {
            Member = members[1], Loan = lateReturnedLoan, Amount = 3.00m,
            Reason = "Returned 6 days late", IssuedAt = lateReturnedLoan.ReturnedAt!.Value,
            IsPaid = false
        },
        new Fine
        {
            Member = members[2], Loan = onTimeReturnedLoan, Amount = 1.50m,
            Reason = "Returned 3 days late (paid)", IssuedAt = onTimeReturnedLoan.ReturnedAt!.Value,
            IsPaid = true, PaidAt = DateTime.UtcNow.AddDays(-14)
        });
    await db.SaveChangesAsync();
    await transaction.CommitAsync();
}
