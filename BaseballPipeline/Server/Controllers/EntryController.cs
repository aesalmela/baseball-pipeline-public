﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using BaseballPipeline.Server.Data;
using BaseballPipeline.Server.Data.Entities;
using BaseballPipeline.Server.Models;
using BaseballPipeline.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebPush;

namespace BaseballPipeline.Server.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class EntryController : ControllerBase
    {
        private readonly AppDBContext context;
        private readonly IMapper mapper;
        private readonly ILogger<TagController> logger;
        //private readonly UserManager<ApplicationUser> userMan;

        public EntryController(AppDBContext context, IMapper mapper, ILogger<TagController> logger)
        {
            this.context = context;
            this.logger = logger;
            this.mapper = mapper;
            //this.userMan = userMan;

        }

        [AllowAnonymous]
        [HttpGet]
        public async Task<ActionResult<IEnumerable<EntryModel>>> GetEntries()
        {
            try
            {
                //var user = await userMan.FindByEmailAsync("andrewsalmela@gmail.com");
                //var result = await userMan.AddToRoleAsync(user, "Admin");
                logger.LogInformation("Getting entries.");
                return await context.Entries.ProjectTo<EntryModel>(mapper.ConfigurationProvider).OrderByDescending(e => e.DateAdded).ToListAsync();
            }
            catch(Exception ex)
            {
                logger.LogCritical(ex, $"{nameof(EntryController)}.{nameof(GetEntries)} caught an exception.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }

        [AllowAnonymous]
        [HttpGet("{id}")]
        public async Task<ActionResult<EntryModel>> GetEntry(int id)
        {
            logger.LogInformation($"Retrieving entry {id}");
            var entry = await context.Entries
                .Include(e => e.EntryTags)
                    .ThenInclude(t => t.Tag)
                .SingleAsync(e => e.EntryId == id);

            if (entry == null)
            {
                return NotFound();
            }
            else
            {
                var model = mapper.Map<EntryModel>(entry);
                return model;
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<ActionResult<EntryModel>> PostEntry(EntryModel entryModel)
        {
            entryModel.DateAdded = DateTime.Now;
            Entry entry = mapper.Map<Entry>(entryModel);
            context.Entries.Add(entry);
            await context.SaveChangesAsync();

            var subscriptions = await context.NotificationSubscriptions.ProjectTo<NotificationSubscriptionModel>(mapper.ConfigurationProvider).ToListAsync();
            logger.LogInformation("Got {subsCount} number of notifications to send.", subscriptions.Count());

            _ = SendNotificationAsync(subscriptions, $"New item \"{entryModel.Title}\" added.");

            return CreatedAtAction(nameof(GetEntry), new { id = entryModel.EntryId }, entryModel);
        }

        [Authorize(Roles = "Admin")]
        [HttpPut]
        public async Task<ActionResult<EntryModel>> Put(EntryModel entryModel)
        {
            Entry entry = mapper.Map<Entry>(entryModel);

            //Tags Added
            foreach (var tag in entry.EntryTags)
            {
                if (!entryModel.ExistingTags.Contains(tag.TagId))
                {
                    context.Entry(tag).State = EntityState.Added;
                }
            }

            //Tags Removed
            foreach (int eTag in entryModel.ExistingTags)
            {
                if (!entry.EntryTags.Select(t => t.TagId).Contains(eTag))
                {
                    context.Entry(new EntryTag() { EntryId = entry.EntryId, TagId = eTag }).State = EntityState.Deleted;
                }
            }

            //Entry Updates
            context.Entry(entry).State = EntityState.Modified;

            //Save Changes
            await context.SaveChangesAsync();

            return entryModel;
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var item = new Entry { EntryId = id };
            context.Remove(item);
            await context.SaveChangesAsync();
            return NoContent();
        }

        private async Task SendNotificationAsync(IEnumerable<NotificationSubscriptionModel> subscriptions, string message)
        {
            var publicKey = "";
            //Move to appsettings and use secrets manager
            //https://www.bartvanuden.com/2018/01/23/push-notifications-to-your-pwa-with-asp-net-core-2-0-and-aurelia/
            var privateKey = "";

            int counter = 1;
            foreach (var subscription in subscriptions)
            {
                try
                {
                    var pushSubscription = new PushSubscription(subscription.Url, subscription.P256dh, subscription.Auth);
                    var vapidDetails = new VapidDetails("mailto:", publicKey, privateKey);
                    var webPushClient = new WebPushClient();

                    var payload = JsonSerializer.Serialize(new
                    {
                        message,
                        url = "https://baseball-pipeline.azurewebsites.net",
                    });
                    await webPushClient.SendNotificationAsync(pushSubscription, payload, vapidDetails);
                    logger.LogInformation($"Sent notification number {counter}.");
                    counter++;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"{nameof(EntryController)}.{nameof(SendNotificationAsync)} caught an exception.");
                }
            }
        }
    }
}
