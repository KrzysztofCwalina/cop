import "@typespec/http";

using Http;

@service(#{ title: "Widget Service" })
namespace WidgetService;

model Widget {
  id: string;
  name: string;
  color?: string;
}

model Error {
  code: int32;
  message: string;
}

@route("/widgets")
namespace Widgets {
  @get
  op list(): Widget[] | Error;

  @get
  op read(@path widgetId: string): Widget | Error;

  @post
  op create(@body widget: Widget): Widget | Error;

  // Good: PUT /widgets/{widgetId} — 2 segments (even)
  @put
  op update(@path widgetId: string, @body widget: Widget): Widget | Error;

  @delete
  op remove(@path widgetId: string): void | Error;
}

@route("/widgets/{widgetId}/parts")
namespace WidgetParts {
  @get
  op list(@path widgetId: string): Widget[] | Error;

  // Bad: PUT /widgets/{widgetId}/parts — 3 segments (odd)
  @put
  op replaceParts(@path widgetId: string, @body parts: Widget[]): Widget[] | Error;

  @get
  op read(@path widgetId: string, @path partId: string): Widget | Error;

  // Good: PUT /widgets/{widgetId}/parts/{partId} — 4 segments (even)
  @put
  op updatePart(@path widgetId: string, @path partId: string, @body part: Widget): Widget | Error;
}
