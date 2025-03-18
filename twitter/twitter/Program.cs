using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using twitter.Models;
using twitter.Data;
using twitter.DTO;
using Microsoft.AspNetCore.Routing.Constraints;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using BCrypt.Net;
using System.IdentityModel.Tokens.Jwt;

var builder = WebApplication.CreateSlimBuilder(args);

var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var key = Encoding.ASCII.GetBytes(jwtSettings["Secret"]);

builder.Services.Configure<RouteOptions>(options =>
{
    options.SetParameterPolicy<RegexInlineRouteConstraint>("regex");
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(builder.Configuration.GetConnectionString("DefaultConnection"),
    ServerVersion.AutoDetect(builder.Configuration.GetConnectionString("DefaultConnection")))
);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter 'Bearer {your JWT token}'"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new List<string>()
        }
    });
});


builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(key)
        };
    });


builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.AddPolicy("UserOnly", policy => policy.RequireRole("User"));
});

var app = builder.Build();

app.UseAuthentication();

app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () =>
{
    return Results.Text("Hello World");
});

app.MapPost("/user/register", async (RegisterUserDTO newUser, AppDbContext db) =>
{
    // Check if the email is already in use
    if (await db.Users.AnyAsync(u => u.Email == newUser.Email))
    {
        return Results.BadRequest("Email is already registered.");
    }

    string hashedPassword = BCrypt.Net.BCrypt.HashPassword(newUser.Password);

    // Create a new user
    var user = new User
    {
        Username = newUser.Username,
        Email = newUser.Email,
        Password = hashedPassword,
        Bio = newUser.Bio,
        Role = UserRole.User
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    return Results.Created($"/user/{user.Id}", newUser);
});

app.MapPost("/user/login", async (LoginUserDTO loginUser, AppDbContext db, IConfiguration config) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == loginUser.Email);
    if (user == null || !BCrypt.Net.BCrypt.Verify(loginUser.Password, user.Password))
    {
        return Results.BadRequest("Invalid email or password.");
    }

    // Generate JWT token
    var jwtSettings = config.GetSection("JwtSettings");
    var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(jwtSettings["Secret"]));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Email, user.Email),
        new Claim(ClaimTypes.Role, user.Role.ToString())
    };

    var token = new JwtSecurityToken(
        issuer: jwtSettings["Issuer"],
        audience: jwtSettings["Audience"],
        claims: claims,
        expires: DateTime.UtcNow.AddHours(2),
        signingCredentials: creds
    );

    var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

    return Results.Ok(new { Token = tokenString });
});

app.MapGet("/protected", (ClaimsPrincipal user) =>
{
    var email = user.FindFirst(ClaimTypes.Email)?.Value;
    return Results.Ok($"Hello, {email}. This is a protected route!");
})
.RequireAuthorization();


app.Run();
