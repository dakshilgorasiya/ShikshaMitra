using static System.Net.Mime.MediaTypeNames;
using System.ComponentModel.DataAnnotations;

namespace twitter.Models
{
    public enum UserRole
    {
        Admin,
        User
    }
    public class User
    {
        [Key]
        public int Id { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public string Password { get; set; }
        public string Bio { get; set; }
        public UserRole Role { get; set; }
        public ICollection<Tweet> Tweets { get; set; }
        public ICollection<Comment> Comments { get; set; }
        public ICollection<Report> Reports { get; set; }
        public ICollection<Follow> Followers { get; set; }
        public ICollection<Follow> Following { get; set; }
        public ICollection<Like> Likes { get; set; }
    }
}
