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
using Microsoft.Extensions.Caching.Memory;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using NLog;
using NLog.Web;

var logger = LogManager.Setup().LoadConfigurationFromFile("nlog.config").GetCurrentClassLogger();
logger.Info("Starting application...");

var builder = WebApplication.CreateSlimBuilder(args);

var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var key = Encoding.ASCII.GetBytes(jwtSettings["Secret"]);

builder.Services.AddMemoryCache();

builder.Logging.ClearProviders();

builder.Host.UseNLog();

builder.Services.Configure<RouteOptions>(options =>
{
    options.SetParameterPolicy<RegexInlineRouteConstraint>("regex");
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(builder.Configuration.GetConnectionString("DefaultConnection"),
    ServerVersion.AutoDetect(builder.Configuration.GetConnectionString("DefaultConnection")))
);

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("fixed", limiterOptions =>
    {
        limiterOptions.Window = TimeSpan.FromSeconds(10); // 10 seconds window
        limiterOptions.PermitLimit = 3; // Allow 5 requests per 10 seconds
        limiterOptions.QueueLimit = 0;  // Queue 2 extra requests before rejecting
    });
});

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

app.UseRateLimiter();

app.UseAuthentication();

app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", (ILogger<Program> log) =>
{
    return Results.Text("Hello World");
});

app.MapPost("/api/v1/user/register", async (RegisterUserDTO newUser, AppDbContext db) =>
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

app.MapPost("/api/v1/user/login", async (LoginUserDTO loginUser, AppDbContext db, IConfiguration config) =>
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

app.MapPost("/api/v1/tweet/create", async (ClaimsPrincipal user, CreateTweetDTO newTweet, AppDbContext db, HttpContext httpContext) =>
{
    // Extract user ID from JWT claims
    var userId = int.Parse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value);

    if (userId == null)
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

    return Results.Created();
}).RequireAuthorization();

app.MapPost("/api/v1/like/tweet", async (TweetLikeDTO like, AppDbContext db, ClaimsPrincipal user) =>
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

app.MapPost("/api/v1/like/comment", async (CommentLikeDTO like, AppDbContext db, ClaimsPrincipal user) =>
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
        return Results.Ok("Comment unliked.");
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
        return Results.Ok("Comment liked.");
    }
}).RequireAuthorization();

app.MapPost("/api/v1/comment/tweet", async (CreateCommentTweetDTO comment, AppDbContext db, ClaimsPrincipal user) =>
{
    var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
    if (userIdClaim == null)
    {
        return Results.Unauthorized();
    }

    int userId = int.Parse(userIdClaim.Value);

    var newComment = new Comment
    {
        Content = comment.Content,
        OwnerId = userId,
        TweetId = comment.TweetId,
    };

    db.Comments.Add(newComment);
    await db.SaveChangesAsync();
    return Results.Ok("Commented");
});

app.MapPost("/api/v1/comment/reply", async (CreateCommentReplyDTO comment, AppDbContext db, ClaimsPrincipal user) =>
{
    var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
    if (userIdClaim == null)
    {
        return Results.Unauthorized();
    }

    int userId = int.Parse(userIdClaim.Value);

    var newComment = new Comment
    {
        Content = comment.Content,
        OwnerId = userId,
        CommentId = comment.CommentId,
    };

    db.Comments.Add(newComment);
    await db.SaveChangesAsync();
    return Results.Ok("Commented");
});

app.MapPost("/api/v1/report/create", async (CreateReportDTO report, AppDbContext db, ClaimsPrincipal user) =>
{
    var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
    if (userIdClaim == null)
    {
        return Results.Unauthorized();
    }

    int userId = int.Parse(userIdClaim.Value);

    var newReport = new Report
    {
        Content = report.Content,
        OwnerId = userId,
        Title = report.Title,
        TweetId = report.TweetId
    };

    db.Reports.Add(newReport);
    await db.SaveChangesAsync();
    return Results.Ok("Reported");
});

app.MapGet("/api/v1/tweet", async (HttpContext context, AppDbContext db, IMemoryCache cache, ILogger<Program> log, int page = 1, int pageSize = 10) =>
{
    if (page < 1) page = 1;
    if (pageSize < 1) pageSize = 10;

    var tweetsQuery = db.Tweets
        .Include(t => t.Owner)
        .Include(t => t.Comments)
        .Include(t => t.Likes)
        .Select(t => new
        {
            Id = t.Id,
            Title = t.Title,
            TweetData = t.TweetData,
            Tags = t.Tags,
            Owner = new
            {
                t.Owner.Id,
                t.Owner.Username,
                t.Owner.Email
            },
            CommentsCount = t.Comments.Count,
            LikesCount = t.Likes.Count
        });

    var totalTweets = await tweetsQuery.CountAsync();
    var tweets = await tweetsQuery
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync();

    var response = new
    {
        TotalTweets = totalTweets,
        Page = page,
        PageSize = pageSize,
        TotalPages = (int)Math.Ceiling((double)totalTweets / pageSize),
        Tweets = tweets
    };

    // Set headers in HttpContext.Response
    context.Response.Headers["Deprecation"] = "Sun, 20 Apr 2025 00:00:00 GMT";
    context.Response.Headers["Link"] = "</api/v2/tweet>; rel=\"successor-version\""; // Optional: Point to a newer version
    context.Response.Headers["Warning"] = "299 - \"This API version will be removed after 20-04-2025. Use /api/v2/tweet instead.\"";

    return Results.Ok(response);
}).RequireRateLimiting("fixed");


app.MapGet("/api/v2/tweet", async (AppDbContext db, IMemoryCache cache, ILogger<Program> log, int page = 1, int pageSize = 10) =>
{
    if (page < 1) page = 1;
    if (pageSize < 1) pageSize = 10;

    string cacheKey = $"tweets_page_{page}size{pageSize}";

    if (!cache.TryGetValue(cacheKey, out object? cachedTweets))
    {

        log.LogInformation("Cache miss");
        var tweetsQuery = db.Tweets
            .Include(t => t.Owner)
            .Include(t => t.Comments)
            .Include(t => t.Likes)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new
            {
                Id = t.Id,
                Title = t.Title,
                TweetData = t.TweetData,
                Tags = t.Tags,
                Owner = new
                {
                    t.Owner.Id,
                    t.Owner.Username,
                    t.Owner.Email
                },
                CommentsCount = t.Comments.Count,
                LikesCount = t.Likes.Count,
                CreatedAt = t.CreatedAt,
            });
           

        var totalTweets = await tweetsQuery.CountAsync();
        var tweets = await tweetsQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var response = new
        {
            TotalTweets = totalTweets,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling((double)totalTweets / pageSize),
            Tweets = tweets
        };

        var cacheEntryOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(5))
            .SetSlidingExpiration(TimeSpan.FromMinutes(2));

        cache.Set(cacheKey, response, cacheEntryOptions);

        return Results.Ok(response);
    }

    log.LogInformation("Cache Hit");

    return Results.Ok(cachedTweets);
}).RequireRateLimiting("fixed");

app.MapGet("/api/v1/tweet/{id}", async (int id, AppDbContext db, ILogger<Program> log, IMemoryCache cache) =>
{

    string cacheKey = $"tweet_{id}";

    if (!cache.TryGetValue(cacheKey, out object? cachedTweet))
    {

        log.LogInformation("Cache miss");
        var tweet = await db.Tweets
            .Where(t => t.Id == id)
            .Include(t => t.Owner) // Include Tweet Owner (User)
            .Include(t => t.Comments) // Include Comments on the Tweet
                .ThenInclude(c => c.Owner) // Include Comment Owners
            .Include(t => t.Likes) // Include Likes on the Tweet
            .Select(t => new
            {
                Id = t.Id,
                Title = t.Title,
                TweetData = t.TweetData,
                Tags = t.Tags,
                Owner = new
                {
                    t.Owner.Id,
                    t.Owner.Username,
                    t.Owner.Email
                },
                Comments = t.Comments.Select(c => new
                {
                    Id = c.Id,
                    Content = c.Content,
                    Owner = new
                    {
                        c.Owner.Id,
                        c.Owner.Username
                    }
                }),
                CommentsCount = t.Comments.Count, // Count total comments
                LikesCount = t.Likes.Count // Count total likes
            })
            .FirstOrDefaultAsync();

        if (tweet == null)
        {
            return Results.NotFound(new { message = "Tweet not found" });
        }

        var cacheEntryOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(5))
            .SetSlidingExpiration(TimeSpan.FromMinutes(2));

        Console.WriteLine("Cache miss");
        cache.Set(cacheKey, tweet, cacheEntryOptions);

        return Results.Ok(tweet);
    }


    log.LogInformation("Cache hit");
    return Results.Ok(cachedTweet);

});

app.MapGet("/api/v1/comment/tweet/{id}", async (int id, AppDbContext db) =>
{
    var comment = await db.Comments.Where(c => c.TweetId == id).Include(t => t.Owner).Include(t => t.Likes).Include(t => t.Replies).Select(t => new
    {
        Id = t.Id,
        Content = t.Content,
        Owner = new
        {
            t.Owner.Id,
            t.Owner.Username,
            t.Owner.Email
        },
        LikesCount = t.Likes.Count,
        Replies = t.Replies.Count,
    }
    ).ToListAsync();

    if (comment == null)
    {
        return Results.NotFound(new { message = "Comments not found" });
    }

    return Results.Ok(comment);

});

app.MapGet("/api/v1/comment/reply/{id}", async (int id, AppDbContext db) =>
{
    var comment = await db.Comments.Where(c => c.CommentId == id).Include(t => t.Owner).Include(t => t.Likes).Include(t => t.Replies).Select(t => new
    {
        Id = t.Id,
        Content = t.Content,
        Owner = new
        {
            t.Owner.Id,
            t.Owner.Username,
            t.Owner.Email
        },
        LikesCount = t.Likes.Count,
        Replies = t.Replies.Count,
    }
    ).ToListAsync();

    if (comment == null)
    {
        return Results.NotFound(new { message = "Comments not found" });
    }

    return Results.Ok(comment);

});

app.MapGet("/api/v1/report/getAllReports", async (AppDbContext db) =>
{
    var reports = await db.Reports.Include(t => t.Tweet).Include(t => t.Owner).Select(t => new
    {
        Id = t.Id,
        Title = t.Title,
        Content = t.Content,
        IsSolved = t.IsSolved,
        Owner = new
        {
            Id = t.Owner.Id,
            Username = t.Owner.Username,
            Email = t.Owner.Email,
        },
        Tweet = new
        {
            Id = t.Tweet.Id,
            Title = t.Tweet.Title,
            Tags = t.Tweet.Tags,
            TweetData = t.Tweet.TweetData,
        }
    }).ToListAsync();

    if (reports == null)
    {
        return Results.NotFound(new { message = "No report found" });
    }

    return Results.Ok(reports);
}).RequireAuthorization("AdminOnly");

app.MapPatch("/api/v1/report/markAsSolved/{id}", async (int id, AppDbContext db) =>
{
    var report = db.Reports.Find(id);

    if (report == null)
    {
        return Results.NotFound();
    }

    report.IsSolved = true;
    await db.SaveChangesAsync();

    return Results.Ok();
}).RequireAuthorization("AdminOnly");

app.Run();
