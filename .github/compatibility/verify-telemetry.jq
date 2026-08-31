def scalar_value:
  .stringValue // .intValue // .doubleValue // .boolValue;

def attribute($name):
  first(.[]? | select(.key == $name) | .value | scalar_value);

def attributes:
  reduce .[]? as $attribute ({}; .[$attribute.key] = ($attribute.value | scalar_value));

[
  .[]
  | .resourceSpans[]?
  | (.resource.attributes | attribute("service.name")) as $service
  | .scopeSpans[].spans[]
  | {
      service: $service,
      traceId,
      spanId,
      name,
      links: (.links // []),
      attributes: (.attributes | attributes)
    }
] as $spans
| ($spans | group_by(.traceId) | map({
    traceId: .[0].traceId,
    services: (map(.service) | unique)
  })) as $traces
| def linked_receives($consumer; $producer):
    [
      $spans[]
      | select(
          .service == $consumer
          and .attributes["messaging.operation.type"] == "receive"
          and any(.links[]?;
            . as $link
            | any($spans[];
                .service == $producer
                and (
                  ($producer == "trogon-eventstore-client-dotnet"
                    and .attributes["db.operation.name"] == "append")
                  or ($producer == "trogon-eventstore-client-rust"
                    and (.attributes["db.operation.name"] == "append_to_stream"
                      or .attributes["db.operation.name"] == "batch_append_to_stream"))
                )
                and .traceId == $link.traceId
                and .spanId == $link.spanId))
        )
    ];
  if any($spans[]; .service == "trogon-eventstore-client-dotnet") then .
  else error("missing C# client spans") end
| if any($spans[]; .service == "trogon-eventstore-client-rust") then .
  else error("missing Rust client spans") end
| if any($spans[]; .service == "eventstore") then .
  else error("missing server spans") end
| if any($spans[];
    .service == "trogon-eventstore-client-dotnet"
    and .attributes["db.system.name"] == "trogoneventstore") then .
  else error("missing C# database semantic convention attributes") end
| if any($spans[];
    .service == "trogon-eventstore-client-rust"
    and .attributes["db.system.name"] == "trogoneventstore") then .
  else error("missing Rust database semantic convention attributes") end
| if any($spans[];
    .service == "trogon-eventstore-client-dotnet"
    and .attributes["messaging.system"] == "trogoneventstore") then .
  else error("missing C# messaging semantic convention attributes") end
| if any($spans[];
    .service == "trogon-eventstore-client-rust"
    and .attributes["messaging.system"] == "trogoneventstore") then .
  else error("missing Rust messaging semantic convention attributes") end
| if (linked_receives("trogon-eventstore-client-dotnet"; "trogon-eventstore-client-rust") | length) > 0 then .
  else error("C# client receive spans do not link to the Rust creation context") end
| if (linked_receives("trogon-eventstore-client-rust"; "trogon-eventstore-client-dotnet") | length) > 0 then .
  else error("Rust client receive spans do not link to the C# creation context") end
| if any($traces[];
    (.services | index("trogon-eventstore-client-dotnet"))
    and (.services | index("eventstore"))) then .
  else error("C# client traces do not reach the server") end
| if any($traces[];
    (.services | index("trogon-eventstore-client-rust"))
    and (.services | index("eventstore"))) then .
  else error("Rust client traces do not reach the server") end
| {
    spans: ($spans | length),
    services: ($spans | map(.service) | unique),
    linkedClientReceives: (
      (linked_receives("trogon-eventstore-client-dotnet"; "trogon-eventstore-client-rust") | length)
      + (linked_receives("trogon-eventstore-client-rust"; "trogon-eventstore-client-dotnet") | length)
    )
  }
