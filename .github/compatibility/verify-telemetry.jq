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
      name,
      attributes: (.attributes | attributes)
    }
] as $spans
| ($spans | group_by(.traceId) | map({
    traceId: .[0].traceId,
    services: (map(.service) | unique)
  })) as $traces
| if any($spans[]; .service == "trogon-eventstore-client-dotnet") then .
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
| if any($traces[];
    (.services | index("trogon-eventstore-client-dotnet"))
    and (.services | index("trogon-eventstore-client-rust"))) then .
  else error("client consumer traces do not continue the producing client trace") end
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
    sharedClientTraces: (
      $traces
      | map(select(
          (.services | index("trogon-eventstore-client-dotnet"))
          and (.services | index("trogon-eventstore-client-rust"))
        ))
      | length
    )
  }
