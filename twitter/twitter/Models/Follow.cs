using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace twitter.Models
{
    public class Follow
    {
        [Key]
        public int Id { get; set; }
        public int FollowedById { get; set; }
        [ForeignKey("FollowedById")]
        public User FollowedBy { get; set; }
        public int FollowedToId { get; set; }
        [ForeignKey("FollowedToId")]
        public User FollowedTo { get; set; }
    }
}
