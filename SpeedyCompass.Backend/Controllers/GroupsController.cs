using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using SpeedyCompass.Backend.Hubs;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpeedyCompass.Backend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class GroupsController : ControllerBase
    {
        private readonly CompassStateManager _state;

        // Inject the Singleton/Scoped state manager via the constructor
        public GroupsController(CompassStateManager state)
        {
            _state = state;
        }

        [HttpGet]
        public async Task<IActionResult> GetGroups(
            [FromQuery] string googleId,
            [FromQuery] string? searchTerm = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            // 1. Build the Base Query with Search Filtering
            var findFluent = string.IsNullOrWhiteSpace(searchTerm)
                ? _state.ActiveGroups.Find(_ => true)
                : _state.ActiveGroups.Find(g => g.GroupName.ToLower().Contains(searchTerm.ToLower()));

            // 2. Execute with Pagination (Limit & Skip)
            var groups = await findFluent
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync();

            var groupList = new List<object>();

            foreach (var g in groups)
            {
                // 3. Count total members associated with this group in the new table
                long memberCount = await _state.GroupMembers.CountDocumentsAsync(m => m.GroupName == g.GroupName);

                // 4. Check if the specific user requesting the list is already in the group
                bool isMember = !string.IsNullOrEmpty(googleId) &&
                                await _state.GroupMembers.Find(m => m.GroupName == g.GroupName && m.GoogleId == googleId).AnyAsync();

                groupList.Add(new
                {
                    GroupName = g.GroupName,
                    MemberCount = (int)memberCount,
                    MaxGroupSize = g.Settings.MaxGroupSize,
                    AdminGoogleId = g.AdminGoogleId,
                    IsMember = isMember
                });
            }

            return Ok(groupList);
        }
    }
}