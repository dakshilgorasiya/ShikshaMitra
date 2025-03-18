using Microsoft.EntityFrameworkCore;
using System.Text;
using twitter.Models;
using twitter.Data;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(builder.Configuration.GetConnectionString("DefaultConnection"),
    ServerVersion.AutoDetect(builder.Configuration.GetConnectionString("DefaultConnection")))
);

var app = builder.Build();

app.Run();
