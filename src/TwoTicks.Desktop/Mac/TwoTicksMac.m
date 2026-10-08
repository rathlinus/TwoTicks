// What TwoTicks asks of macOS itself: the icon in the menu bar with its
// menu, notifications with buttons and a box to reply in, the count on the
// Dock icon, and the click on the Dock icon that brings a closed window back.
//
// These are Objective-C interfaces, which .NET has no way into, so this small
// library wraps them in C functions. The project builds it on a Mac with
// clang and puts it next to the app; see TwoTicks.Desktop.csproj. The C#
// side is Mac/MacNative.cs.
//
// Lists come in as text, one entry per line with a tab between its parts,
// which keeps the functions down to plain strings.

#import <Cocoa/Cocoa.h>
#import <UserNotifications/UserNotifications.h>

typedef void (*wa_id_callback)(int id);
typedef void (*wa_callback)(void);
typedef void (*wa_notification_callback)(const char *arguments, const char *reply);

static NSString *Text(const char *utf8) {
    return utf8 ? ([NSString stringWithUTF8String:utf8] ?: @"") : @"";
}

// Everything here touches AppKit, which wants the main thread.
static void OnMain(dispatch_block_t block) {
    if ([NSThread isMainThread]) {
        block();
    } else {
        dispatch_async(dispatch_get_main_queue(), block);
    }
}

// The lines of a list, each split at its tabs.
static NSArray<NSArray<NSString *> *> *Rows(NSString *list) {
    NSMutableArray *rows = [NSMutableArray array];
    for (NSString *line in [list componentsSeparatedByString:@"\n"]) {
        if (line.length > 0) {
            [rows addObject:[line componentsSeparatedByString:@"\t"]];
        }
    }
    return rows;
}

// ---- The icon in the menu bar ----

@interface WAMenuTarget : NSObject
@end

static NSStatusItem *statusItem;
static WAMenuTarget *menuTarget;
static wa_id_callback menuClicked;

@implementation WAMenuTarget

- (void)clicked:(NSMenuItem *)sender {
    if (menuClicked) {
        menuClicked((int)sender.tag);
    }
}

@end

static NSImage *StatusImage(NSString *path) {
    NSImage *image = [[NSImage alloc] initWithContentsOfFile:path];
    // The height of the menu bar's icons, whatever sizes the file holds.
    image.size = NSMakeSize(18, 18);
    return image;
}

// menu: one line per entry, "id<tab>text"; a line with an empty text is a separator.
static NSMenu *BuildMenu(NSString *list) {
    NSMenu *menu = [[NSMenu alloc] init];
    for (NSArray<NSString *> *row in Rows(list)) {
        NSString *title = row.count > 1 ? row[1] : @"";
        if (title.length == 0) {
            [menu addItem:[NSMenuItem separatorItem]];
            continue;
        }
        NSMenuItem *item = [[NSMenuItem alloc] initWithTitle:title action:@selector(clicked:) keyEquivalent:@""];
        item.target = menuTarget;
        item.tag = row[0].integerValue;
        [menu addItem:item];
    }
    return menu;
}

void wa_status_show(const char *iconPath, const char *toolTip, const char *menu, wa_id_callback clicked) {
    NSString *path = Text(iconPath);
    NSString *tip = Text(toolTip);
    NSString *list = Text(menu);
    OnMain(^{
        menuClicked = clicked;
        if (!menuTarget) {
            menuTarget = [[WAMenuTarget alloc] init];
        }
        if (!statusItem) {
            statusItem = [[NSStatusBar systemStatusBar] statusItemWithLength:NSSquareStatusItemLength];
        }
        statusItem.button.image = StatusImage(path);
        statusItem.button.toolTip = tip;
        statusItem.menu = BuildMenu(list);
    });
}

void wa_status_set_icon(const char *iconPath) {
    NSString *path = Text(iconPath);
    OnMain(^{
        statusItem.button.image = StatusImage(path);
    });
}

void wa_status_set_tooltip(const char *toolTip) {
    NSString *tip = Text(toolTip);
    OnMain(^{
        statusItem.button.toolTip = tip;
    });
}

void wa_status_set_menu(const char *menu) {
    NSString *list = Text(menu);
    OnMain(^{
        if (statusItem) {
            statusItem.menu = BuildMenu(list);
        }
    });
}

void wa_status_remove(void) {
    OnMain(^{
        if (statusItem) {
            [[NSStatusBar systemStatusBar] removeStatusItem:statusItem];
            statusItem = nil;
        }
    });
}

// ---- The Dock ----

void wa_dock_set_badge(const char *text) {
    NSString *label = Text(text);
    OnMain(^{
        NSApp.dockTile.badgeLabel = label.length > 0 ? label : nil;
    });
}

@interface WAReopenTarget : NSObject
@end

static WAReopenTarget *reopenTarget;
static wa_callback reopened;

@implementation WAReopenTarget

- (void)reopen:(NSAppleEventDescriptor *)event withReply:(NSAppleEventDescriptor *)reply {
    if (reopened) {
        reopened();
    }
}

@end

// A click on the Dock icon, or a second start from the Finder, while the app runs.
void wa_app_on_reopen(wa_callback callback) {
    OnMain(^{
        reopened = callback;
        if (!reopenTarget) {
            reopenTarget = [[WAReopenTarget alloc] init];
            [[NSAppleEventManager sharedAppleEventManager] setEventHandler:reopenTarget
                                                                andSelector:@selector(reopen:withReply:)
                                                              forEventClass:kCoreEventClass
                                                                 andEventID:kAEReopenApplication];
        }
    });
}

// ---- Notifications ----

@interface WANotificationDelegate : NSObject <UNUserNotificationCenterDelegate>
@end

static WANotificationDelegate *notificationDelegate;
static wa_notification_callback notificationActivated;
static NSMutableDictionary<NSString *, UNNotificationCategory *> *categories;

@implementation WANotificationDelegate

// Also while the app is in front: it only notifies about chats that are not open.
- (void)userNotificationCenter:(UNUserNotificationCenter *)center
       willPresentNotification:(UNNotification *)notification
         withCompletionHandler:(void (^)(UNNotificationPresentationOptions))completionHandler {
    UNNotificationPresentationOptions options = UNNotificationPresentationOptionBanner | UNNotificationPresentationOptionList;
    if (notification.request.content.sound) {
        options |= UNNotificationPresentationOptionSound;
    }
    completionHandler(options);
}

- (void)userNotificationCenter:(UNUserNotificationCenter *)center
didReceiveNotificationResponse:(UNNotificationResponse *)response
         withCompletionHandler:(void (^)(void))completionHandler {
    NSString *action = response.actionIdentifier;
    if (![action isEqualToString:UNNotificationDismissActionIdentifier]) {
        // What each button means travels with the notification, under the button's name.
        NSString *key = [action isEqualToString:UNNotificationDefaultActionIdentifier] ? @"default" : action;
        id arguments = response.notification.request.content.userInfo[key];
        NSString *reply = nil;
        if ([response isKindOfClass:[UNTextInputNotificationResponse class]]) {
            reply = ((UNTextInputNotificationResponse *)response).userText;
        }
        if ([arguments isKindOfClass:[NSString class]] && notificationActivated) {
            notificationActivated(((NSString *)arguments).UTF8String, reply.UTF8String);
        }
    }
    completionHandler();
}

@end

// Returns 0 where macOS takes no notifications from the app: when it does not
// run from an app bundle, which is how the notification centre knows an app.
int wa_notifications_start(wa_notification_callback activated) {
    if (NSBundle.mainBundle.bundleIdentifier == nil) {
        return 0;
    }
    notificationActivated = activated;
    notificationDelegate = [[WANotificationDelegate alloc] init];
    categories = [NSMutableDictionary dictionary];
    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    center.delegate = notificationDelegate;
    [center requestAuthorizationWithOptions:(UNAuthorizationOptionAlert | UNAuthorizationOptionSound | UNAuthorizationOptionBadge)
                          completionHandler:^(BOOL granted, NSError *error) {
        if (!granted) {
            NSLog(@"TwoTicks: notifications are not allowed: %@", error.localizedDescription ?: @"turned off in System Settings");
        }
    }];
    return 1;
}

// A kind of notification with its buttons. actions: one line per button,
// "name<tab>text<tab>kind", where kind is empty for a button that does its
// work without the app coming up, "front" for one that brings the app to the
// front, and "reply" for the box to reply in, with its placeholder after it.
// Done at once, on whichever thread asks: the kind has to be known before the
// first notification of it is handed over.
void wa_notifications_set_category(const char *identifier, const char *actions) {
    if (!categories) {
        return;
    }
    NSString *name = Text(identifier);
    NSMutableArray<UNNotificationAction *> *buttons = [NSMutableArray array];
    for (NSArray<NSString *> *row in Rows(Text(actions))) {
        if (row.count < 2) {
            continue;
        }
        NSString *kind = row.count > 2 ? row[2] : @"";
        if ([kind isEqualToString:@"reply"]) {
            [buttons addObject:[UNTextInputNotificationAction actionWithIdentifier:row[0]
                                                                             title:row[1]
                                                                           options:UNNotificationActionOptionNone
                                                              textInputButtonTitle:row[1]
                                                              textInputPlaceholder:row.count > 3 ? row[3] : @""]];
        } else {
            UNNotificationActionOptions options = [kind isEqualToString:@"front"] ? UNNotificationActionOptionForeground : UNNotificationActionOptionNone;
            [buttons addObject:[UNNotificationAction actionWithIdentifier:row[0] title:row[1] options:options]];
        }
    }
    @synchronized (categories) {
        categories[name] = [UNNotificationCategory categoryWithIdentifier:name
                                                                   actions:buttons
                                                         intentIdentifiers:@[]
                                                                   options:UNNotificationCategoryOptionNone];
        [[UNUserNotificationCenter currentNotificationCenter] setNotificationCategories:[NSSet setWithArray:categories.allValues]];
    }
}

// arguments: one line per button name, "name<tab>what it means", with "default" for the click on the notification.
void wa_notifications_show(const char *identifier, const char *category, const char *thread, const char *title,
                           const char *body, const char *picture, int sound, const char *arguments) {
    if (!notificationDelegate) {
        return;
    }
    NSString *name = Text(identifier);
    NSString *picturePath = Text(picture);
    UNMutableNotificationContent *content = [[UNMutableNotificationContent alloc] init];
    content.title = Text(title);
    content.body = Text(body);
    content.categoryIdentifier = Text(category);
    content.threadIdentifier = Text(thread);
    if (sound) {
        content.sound = [UNNotificationSound defaultSound];
    }
    NSMutableDictionary *info = [NSMutableDictionary dictionary];
    for (NSArray<NSString *> *row in Rows(Text(arguments))) {
        if (row.count == 2) {
            info[row[0]] = row[1];
        }
    }
    content.userInfo = info;

    if (picturePath.length > 0) {
        // The notification centre takes the file away, so it gets a copy.
        NSString *extension = picturePath.pathExtension.length > 0 ? picturePath.pathExtension : @"jpg";
        NSString *copy = [NSTemporaryDirectory() stringByAppendingPathComponent:
            [NSString stringWithFormat:@"twoticks-%@.%@", [NSUUID UUID].UUIDString, extension]];
        if ([[NSFileManager defaultManager] copyItemAtPath:picturePath toPath:copy error:nil]) {
            UNNotificationAttachment *attachment = [UNNotificationAttachment attachmentWithIdentifier:@"picture"
                                                                                                  URL:[NSURL fileURLWithPath:copy]
                                                                                              options:nil
                                                                                                error:nil];
            if (attachment) {
                content.attachments = @[attachment];
            }
        }
    }

    UNNotificationRequest *request = [UNNotificationRequest requestWithIdentifier:name content:content trigger:nil];
    [[UNUserNotificationCenter currentNotificationCenter] addNotificationRequest:request withCompletionHandler:^(NSError *error) {
        if (error) {
            NSLog(@"TwoTicks: a notification was not shown: %@", error.localizedDescription);
        }
    }];
}

// identifiers: one per line.
void wa_notifications_remove(const char *identifiers) {
    NSMutableArray<NSString *> *names = [NSMutableArray array];
    for (NSString *line in [Text(identifiers) componentsSeparatedByString:@"\n"]) {
        if (line.length > 0) {
            [names addObject:line];
        }
    }
    if (names.count > 0 && notificationDelegate) {
        UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
        [center removeDeliveredNotificationsWithIdentifiers:names];
        [center removePendingNotificationRequestsWithIdentifiers:names];
    }
}

void wa_notifications_remove_all(void) {
    if (!notificationDelegate) {
        return;
    }
    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    [center removeAllDeliveredNotifications];
    [center removeAllPendingNotificationRequests];
}
