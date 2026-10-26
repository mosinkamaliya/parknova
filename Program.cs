using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();

var parking = new List<ParkingLocation>
{
    new(1, "Crystal Mall Parking", "Kalawad Road", 30, 12, 12, 22.3039, 70.8022),
    new(2, "City Centre Parking", "150 Feet Ring Road", 25, 10, 10, 22.2871, 70.7696),
    new(3, "Orbit Plaza Parking", "Mavdi Circle", 20, 14, 14, 22.2764, 70.7894),
    new(4, "RMC Smart Parking", "Race Course", 20, 8, 8, 22.3072, 70.8008)
};
var bookings = new List<Booking>();
var gate = new object();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, service = "ParkNova" }));
app.MapGet("/api/parking", () => Results.Ok(parking));
app.MapGet("/api/bookings", () => Results.Ok(bookings.OrderByDescending(x => x.CreatedAt)));

app.MapPost("/api/login", ([FromBody] LoginRequest req) =>
{
    var email = req.Email?.Trim() ?? "";
    var password = req.Password ?? "";
    var role = (req.Role ?? "user").Trim().ToLowerInvariant();

    if (role == "admin" && (email.Equals("admin@parknova.com", StringComparison.OrdinalIgnoreCase) || email.Equals("admin", StringComparison.OrdinalIgnoreCase)) && password == "admin123")
        return Results.Ok(new { ok = true, role = "admin", name = "Admin", email = "admin@parknova.com" });

    if (role == "user" && email.Equals("demo@parknova.com", StringComparison.OrdinalIgnoreCase) && password == "123456")
        return Results.Ok(new { ok = true, role = "user", name = "Mosin", email = "demo@parknova.com" });

    return Results.Unauthorized();
});

app.MapPost("/api/bookings", ([FromBody] CreateBookingRequest req) =>
{
    lock (gate)
    {
        var p = parking.FirstOrDefault(x => x.Id == req.ParkingId);
        if (p is null) return Results.NotFound(new { message = "Parking not found" });
        if (p.Free <= 0) return Results.BadRequest(new { message = "Parking is full" });
        if (req.Duration is < 1 or > 24) return Results.BadRequest(new { message = "Duration must be between 1 and 24 hours" });
        if (string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new { message = "Email is required" });
        if (!DateTime.TryParse(req.Date, out var bookingDate) || bookingDate.Date < DateTime.Today) return Results.BadRequest(new { message = "Past dates are not allowed" });
        if (!TimeSpan.TryParse(req.Time, out var bookingTime) || bookingTime < TimeSpan.Zero || bookingTime >= TimeSpan.FromDays(1)) return Results.BadRequest(new { message = "Time must be in 24-hour HH:mm format" });

        var slot = req.ParkingNo ?? NextAvailableSlot(p.Id);
        if (slot < 1 || slot > p.Total) return Results.BadRequest(new { message = "Invalid parking slot number" });
        var occupied = bookings.Any(x => x.Location == p.Name && x.ParkingNo == slot && x.Status != "Cancelled" && x.Status != "Completed");
        if (occupied) return Results.Conflict(new { message = "That parking slot is already booked. Please choose another slot." });
        var b = new Booking(NewBookingId(), req.Email.Trim(), p.Name, slot, req.Date, req.Time, req.Duration,
            p.Price * req.Duration, "Confirmed", DateTime.UtcNow);
        bookings.Insert(0, b);
        p.Free--;
        return Results.Ok(b);
    }
});

app.MapDelete("/api/bookings/{id}", (string id) =>
{
    lock (gate)
    {
        var b = bookings.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (b is null) return Results.NotFound();
        if (b.Status != "Cancelled" && b.Status != "Completed")
        {
            var p = parking.FirstOrDefault(x => x.Name == b.Location);
            if (p is not null) p.Free = Math.Min(p.Total, p.Free + 1);
        }
        bookings.Remove(b);
        return Results.Ok(new { ok = true });
    }
});

app.MapGet("/api/admin/dashboard", () => Results.Ok(new
{
    freeSlots = parking.Sum(x => x.Free),
    occupied = parking.Sum(x => x.Total - x.Free),
    reservations = bookings.Count,
    activeReservations = bookings.Count(x => x.Status == "Confirmed"),
    completed = bookings.Count(x => x.Status == "Completed"),
    cancelled = bookings.Count(x => x.Status == "Cancelled"),
    revenue = bookings.Where(x => x.Status != "Cancelled").Sum(x => x.Total),
    locations = parking.Count,
    apiStatus = "Online"
}));

app.MapPut("/api/admin/parking/{id:int}", (int id, [FromBody] UpdateParkingRequest req) =>
{
    lock (gate)
    {
        var p = parking.FirstOrDefault(x => x.Id == id);
        if (p is null) return Results.NotFound();
        if (req.Total is not null && req.Total.Value < 1) return Results.BadRequest(new { message = "Total must be at least 1" });
        if (req.Price is not null && req.Price.Value < 0) return Results.BadRequest(new { message = "Price cannot be negative" });
        if (req.Free is not null && (req.Free.Value < 0 || req.Free.Value > (req.Total ?? p.Total))) return Results.BadRequest(new { message = "Invalid free slot count" });

        if (req.Total is not null) p.Total = req.Total.Value;
        if (req.Free is not null) p.Free = req.Free.Value;
        else p.Free = Math.Min(p.Free, p.Total);
        if (req.Price is not null) p.Price = req.Price.Value;
        if (!string.IsNullOrWhiteSpace(req.Name)) p.Name = req.Name.Trim();
        if (!string.IsNullOrWhiteSpace(req.Area)) p.Area = req.Area.Trim();
        if (req.Lat is not null) p.Lat = req.Lat.Value;
        if (req.Lng is not null) p.Lng = req.Lng.Value;
        return Results.Ok(p);
    }
});

app.MapPost("/api/admin/parking", ([FromBody] CreateParkingRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || req.Total < 1 || req.Price < 0)
        return Results.BadRequest(new { message = "Invalid parking details" });

    lock (gate)
    {
        var id = parking.Count == 0 ? 1 : parking.Max(x => x.Id) + 1;
        var p = new ParkingLocation(id, req.Name.Trim(), req.Area?.Trim() ?? "", req.Price, req.Total, req.Total, req.Lat, req.Lng);
        parking.Add(p);
        return Results.Ok(p);
    }
});

app.MapDelete("/api/admin/parking/{id:int}", (int id) =>
{
    lock (gate)
    {
        if (parking.Count <= 1) return Results.BadRequest(new { message = "At least one location is required" });
        var p = parking.FirstOrDefault(x => x.Id == id);
        if (p is null) return Results.NotFound();
        parking.Remove(p);
        return Results.Ok(new { ok = true });
    }
});

app.MapPost("/api/admin/bookings", ([FromBody] AdminBookingRequest req) =>
{
    lock (gate)
    {
        var p = parking.FirstOrDefault(x => x.Id == req.ParkingId);
        if (p is null) return Results.NotFound(new { message = "Parking not found" });
        if (p.Free <= 0) return Results.BadRequest(new { message = "No free slot available" });
        if (req.Duration is < 1 or > 24) return Results.BadRequest(new { message = "Duration must be between 1 and 24 hours" });
        if (string.IsNullOrWhiteSpace(req.Customer)) return Results.BadRequest(new { message = "Customer is required" });
        if (!DateTime.TryParse(req.Date, out var bookingDate) || bookingDate.Date < DateTime.Today) return Results.BadRequest(new { message = "Past dates are not allowed" });
        if (!TimeSpan.TryParse(req.Time, out var bookingTime) || bookingTime < TimeSpan.Zero || bookingTime >= TimeSpan.FromDays(1)) return Results.BadRequest(new { message = "Time must be in 24-hour HH:mm format" });

        var slot = req.ParkingNo ?? NextAvailableSlot(p.Id);
        if (slot < 1 || slot > p.Total) return Results.BadRequest(new { message = "Invalid parking slot number" });
        var occupied = bookings.Any(x => x.Location == p.Name && x.ParkingNo == slot && x.Status != "Cancelled" && x.Status != "Completed");
        if (occupied) return Results.Conflict(new { message = "That parking slot is already booked. Please choose another slot." });
        var b = new Booking(NewBookingId(), req.Customer.Trim(), p.Name, slot, req.Date, req.Time, req.Duration,
            p.Price * req.Duration, "Confirmed", DateTime.UtcNow);
        bookings.Insert(0, b);
        p.Free--;
        return Results.Ok(b);
    }
});

app.MapPut("/api/admin/bookings/{id}", (string id, [FromBody] BookingStatusRequest req) =>
{
    lock (gate)
    {
        var b = bookings.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (b is null) return Results.NotFound();
        var newStatus = req.Status?.Trim() ?? "";
        if (newStatus is not ("Confirmed" or "Completed" or "Cancelled"))
            return Results.BadRequest(new { message = "Invalid status" });

        if (b.Status != "Cancelled" && b.Status != "Completed" && (newStatus == "Cancelled" || newStatus == "Completed"))
        {
            var p = parking.FirstOrDefault(x => x.Name == b.Location);
            if (p is not null) p.Free = Math.Min(p.Total, p.Free + 1);
        }
        else if (b.Status == "Cancelled" && newStatus == "Confirmed")
        {
            var p = parking.FirstOrDefault(x => x.Name == b.Location);
            if (p is null || p.Free <= 0) return Results.BadRequest(new { message = "No free slot available" });
            p.Free--;
        }

        b.Status = newStatus;
        return Results.Ok(b);
    }
});

app.Run();

int NextAvailableSlot(int parkingId)
{
    var p = parking.First(x => x.Id == parkingId);
    var used = bookings
        .Where(b => b.Location == p.Name && b.Status != "Cancelled" && b.Status != "Completed")
        .Select(b => b.ParkingNo)
        .ToHashSet();
    for (var i = 1; i <= p.Total; i++)
        if (!used.Contains(i)) return i;
    return 1;
}

static string NewBookingId() => $"PN-{Random.Shared.Next(100000, 999999)}";

record LoginRequest(string Email, string Password, string Role);
record CreateBookingRequest(int ParkingId, string Email, string Date, string Time, int Duration, int? ParkingNo = null);
record AdminBookingRequest(int ParkingId, string Customer, string Date, string Time, int Duration, int? ParkingNo = null);
record BookingStatusRequest(string Status);
record UpdateParkingRequest(int? Free, int? Total, int? Price, string? Name, string? Area, double? Lat, double? Lng);
record CreateParkingRequest(string Name, string? Area, int Price, int Total, double Lat, double Lng);
record Booking(string Id, string User, string Location, int ParkingNo, string Date, string Time, int Duration, int Total, string Status, DateTime CreatedAt)
{
    public string Status { get; set; } = Status;
};
class ParkingLocation(int id, string name, string area, int price, int free, int total, double lat, double lng)
{
    public int Id { get; set; } = id;
    public string Name { get; set; } = name;
    public string Area { get; set; } = area;
    public int Price { get; set; } = price;
    public int Free { get; set; } = free;
    public int Total { get; set; } = total;
    public double Lat { get; set; } = lat;
    public double Lng { get; set; } = lng;
}
