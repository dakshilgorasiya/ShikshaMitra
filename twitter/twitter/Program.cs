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
using Microsoft.AspNetCore.Authorization;

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

app.MapPost("/tweet/create", async (ClaimsPrincipal user, CreateTweetDTO newTweet, AppDbContext db, HttpContext httpContext) =>
{
    // Extract user ID from JWT claims
    var userId = int.Parse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value);

    if(userId == null)
    {
        return Results.Unauthorized();
    }

    // Create a new Tweet
    var tweet = new Tweet
    {
        OwnerId = userId,
        Title = newTweet.Title,
        Tags = newTweet.Tags,
        TweetData = newTweet.TweetData,
    };

    db.Tweets.Add(tweet);
    await db.SaveChangesAsync();

    return Results.Created($"/tweet/{tweet.Id}", tweet);
}).RequireAuthorization();

app.MapPost("/like/tweet", async (TweetLikeDTO like, AppDbContext db, ClaimsPrincipal user) =>
{
    var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
    if (userIdClaim == null)
    {
        return Results.Unauthorized();
    }

    int userId = int.Parse(userIdClaim.Value);

    // Check if the user already liked the tweet
    var existingLike = await db.Likes.FirstOrDefaultAsync(l => l.TweetId == like.TweetId && l.OwnerId == userId);

    if (existingLike != null)
    {
        // Unlike (remove the like)
        db.Likes.Remove(existingLike);
        await db.SaveChangesAsync();
        return Results.Ok("Tweet unliked.");
    }
    else
    {
        // Like (add new like)
        var newLike = new Like
        {
            TweetId = like.TweetId,
            OwnerId = userId
        };

        db.Likes.Add(newLike);
        await db.SaveChangesAsync();
        return Results.Ok("Tweet liked.");
    }
}).RequireAuthorization();

app.MapPost("/like/comment", async (CommentLikeDTO like, AppDbContext db, ClaimsPrincipal user) =>
{
    var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
    if (userIdClaim == null)
    {
        return Results.Unauthorized();
    }

    int userId = int.Parse(userIdClaim.Value);

    // Check if the user already liked the tweet
    var existingLike = await db.Likes.FirstOrDefaultAsync(l => l.TweetId == like.CommentId && l.OwnerId == userId);

    if (existingLike != null)
    {
        // Unlike (remove the like)
        db.Likes.Remove(existingLike);
        await db.SaveChangesAsync();
        return Results.Ok("Tweet unliked.");
    }
    else
    {
        // Like (add new like)
        var newLike = new Like
        {
            CommentId = like.CommentId,
            OwnerId = userId
        };

        db.Likes.Add(newLike);
        await db.SaveChangesAsync();
        return Results.Ok("Tweet liked.");
    }
}).RequireAuthorization();

app.MapGet("/comment/tweet", async (CreateCommentTweetDTO comment, AppDbContext db, ClaimsPrincipal user) => {
    var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
    if (userIdClaim == null)
    {
        return Results.Unauthorized();
    }

    int userId = int.Parse(userIdClaim.Value);

    var newComment = new Comment
    {
        Content = comment.Content,
        OwnerId= userId,
        TweetId = comment.TweetId,
    };

    db.Comments.Add(newComment);
    await db.SaveChangesAsync();
    return Results.Ok("Commented");
});

app.Run();
