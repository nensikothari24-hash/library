using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LibraryManagement;

public sealed class LibrarianUser : IdentityUser { }

public sealed class LibraryDb(DbContextOptions<LibraryDb> options) : IdentityDbContext<LibrarianUser>(options)
{
    public DbSet<Book> Books => Set<Book>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<Fine> Fines => Set<Fine>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Book>().HasIndex(book => book.Isbn).IsUnique();
        builder.Entity<Member>().HasIndex(member => member.Email).IsUnique();
        builder.Entity<Member>().HasIndex(member => member.MemberNumber).IsUnique();
        builder.Entity<Category>().HasIndex(category => category.Name).IsUnique();
        builder.Entity<Book>().HasOne(book => book.Category).WithMany().HasForeignKey(book => book.CategoryId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<Loan>().HasOne(loan => loan.Book).WithMany().HasForeignKey(loan => loan.BookId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Loan>().HasOne(loan => loan.Member).WithMany().HasForeignKey(loan => loan.MemberId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Reservation>().HasOne(item => item.Book).WithMany().HasForeignKey(item => item.BookId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Reservation>().HasOne(item => item.Member).WithMany().HasForeignKey(item => item.MemberId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Fine>().HasOne(fine => fine.Member).WithMany().HasForeignKey(fine => fine.MemberId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Fine>().HasOne(fine => fine.Loan).WithMany().HasForeignKey(fine => fine.LoanId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Fine>().Property(fine => fine.Amount).HasPrecision(10, 2);
    }
}

public sealed class Book
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string Author { get; set; }
    public required string Isbn { get; set; }
    public string Publisher { get; set; } = "";
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    public int PublishedYear { get; set; }
    public int TotalCopies { get; set; }
    public int AvailableCopies { get; set; }
    public string Description { get; set; } = "";
}

public sealed class Category
{
    public int Id { get; set; }
    public required string Name { get; set; }
}

public sealed class Member
{
    public int Id { get; set; }
    public required string FullName { get; set; }
    public required string Email { get; set; }
    public required string Phone { get; set; }
    public required string MemberNumber { get; set; }
    public string Address { get; set; } = "";
    public DateTime JoinedAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Loan
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public Book Book { get; set; } = null!;
    public int MemberId { get; set; }
    public Member Member { get; set; } = null!;
    public DateTime CheckedOutAt { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public int RenewalCount { get; set; }
}

public sealed class Reservation
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public Book Book { get; set; } = null!;
    public int MemberId { get; set; }
    public Member Member { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Pending;
}

public sealed class Fine
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public Member Member { get; set; } = null!;
    public int LoanId { get; set; }
    public Loan Loan { get; set; } = null!;
    public decimal Amount { get; set; }
    public required string Reason { get; set; }
    public DateTime IssuedAt { get; set; }
    public bool IsPaid { get; set; }
    public DateTime? PaidAt { get; set; }
}

public enum ReservationStatus { Pending, ReadyForPickup, Fulfilled, Cancelled }

public sealed record LoginRequest(string Email, string Password, bool RememberMe);
public sealed record BookInput(string Title, string Author, string Isbn, string? Publisher, int? CategoryId,
    int PublishedYear, int TotalCopies, string? Description);
public sealed record CategoryInput(string Name);
public sealed record MemberInput(string FullName, string Email, string Phone, string MemberNumber, string? Address, bool IsActive = true);
public sealed record CheckoutInput(int BookId, int MemberId, int? ReservationId = null);
public sealed record ReservationInput(int BookId, int MemberId);
