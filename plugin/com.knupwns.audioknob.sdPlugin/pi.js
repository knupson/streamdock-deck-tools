// Shared property-inspector glue.
//
// The host calls one of three entry points with the socket details, depending on
// its version. A page registers a render callback plus an optional handler for
// data the plugin pushes, and uses PI.set() to persist a setting.

const PI = (function () {
  let socket = null;
  let piUuid = null;
  let actionUuid = null;
  let actionContext = null;
  let settings = {};

  let onRender = function () {};
  let onData = function () {};
  let pending = [];

  function open(port, uuid, registerEvent, appInfo, actionInfo) {
    piUuid = uuid;

    const info = actionInfo ? JSON.parse(actionInfo) : {};
    actionUuid = info.action;
    actionContext = info.context;
    settings = (info.payload && info.payload.settings) || {};

    socket = new WebSocket('ws://127.0.0.1:' + port);
    socket.onopen = function () {
      socket.send(JSON.stringify({ event: registerEvent, uuid: uuid }));
      onRender(settings);
      pending.forEach(function (message) { send(message); });
      pending = [];
    };
    socket.onmessage = function (event) {
      const data = JSON.parse(event.data);
      if (data.event === 'didReceiveSettings') {
        settings = (data.payload && data.payload.settings) || {};
        onRender(settings);
      } else if (data.event === 'sendToPropertyInspector') {
        onData(data.payload || {});
      }
    };
  }

  function send(message) {
    if (!socket || socket.readyState !== 1) { pending.push(message); return; }
    socket.send(JSON.stringify(message));
  }

  // `extra` merges additional fields into the payload alongside `command` -
  // e.g. ask('authorize', { clientId: x, clientSecret: y }). Omit it and this
  // is exactly the old { command: command } shape every other page already
  // relies on.
  function ask(command, extra) {
    const payload = extra ? Object.assign({ command: command }, extra) : { command: command };
    send({ event: 'sendToPlugin', action: actionUuid, context: piUuid, payload: payload });
  }

  function save() {
    socket.send(JSON.stringify({
      event: 'setSettings', context: actionContext, payload: settings
    }));
    socket.send(JSON.stringify({
      event: 'sendToPlugin', action: actionUuid, context: piUuid, payload: { settings: settings }
    }));
  }

  function option(value, text, selected) {
    const element = document.createElement('option');
    element.value = value;
    element.textContent = text;
    element.selected = !!selected;
    return element;
  }

  return {
    open: open,
    ask: ask,

    ready: function (render, data) {
      onRender = render || onRender;
      onData = data || onData;
    },

    settings: function () { return settings; },

    set: function (key, value) {
      settings[key] = value;
      save();
    },

    // Fills a <select> from [{id, name}], keeping a stored-but-missing choice
    // visible instead of silently reassigning it to another entry. Built as DOM
    // nodes: these names come from device and process enumeration, not markup.
    fill: function (select, items, chosen, emptyLabel) {
      const known = items.some(function (item) { return item.id === chosen; });

      select.textContent = '';
      if (emptyLabel) select.appendChild(option('', emptyLabel, !chosen));

      items.forEach(function (item) {
        select.appendChild(option(item.id, item.name, item.id === chosen));
      });

      if (chosen && !known) select.appendChild(option(chosen, '(unavailable)', true));
    },

    // Wires a group of radio inputs to one setting.
    radios: function (name, key, fallback, afterChange) {
      const inputs = document.querySelectorAll('input[name="' + name + '"]');
      inputs.forEach(function (input) {
        input.addEventListener('change', function () {
          PI.set(key, input.value);
          if (afterChange) afterChange(input.value);
        });
      });
      return function (value) {
        const chosen = value || fallback;
        inputs.forEach(function (input) { input.checked = input.value === chosen; });
        if (afterChange) afterChange(chosen);
      };
    },

    // Wires a number input with clamping to one setting.
    number: function (id, key, fallback, min, max) {
      const input = document.getElementById(id);
      input.addEventListener('change', function () {
        let value = parseInt(input.value, 10);
        if (isNaN(value)) value = fallback;
        value = Math.min(max, Math.max(min, value));
        input.value = value;
        PI.set(key, value);
      });
      return function (value) { input.value = value || fallback; };
    },

    // Wires a text input to one setting, saving when it loses focus or on Enter.
    text: function (id, key, fallback) {
      const input = document.getElementById(id);
      const commit = function () { PI.set(key, input.value.trim()); };

      input.addEventListener('change', commit);
      input.addEventListener('keydown', function (event) {
        if (event.key === 'Enter') { commit(); input.blur(); }
      });
      return function (value) { input.value = value || fallback || ''; };
    },

    button: function (id, command) {
      document.getElementById(id).addEventListener('click', function () { PI.ask(command); });
    },

    checkbox: function (id, key, fallback) {
      const input = document.getElementById(id);
      input.addEventListener('change', function () { PI.set(key, input.checked); });
      return function (value) { input.checked = value === undefined ? fallback : !!value; };
    }
  };
})();

function connectElgatoStreamDeckSocket(port, uuid, registerEvent, appInfo, actionInfo) {
  PI.open(port, uuid, registerEvent, appInfo, actionInfo);
}
const connectSocket = connectElgatoStreamDeckSocket;
const connectMiraBoxSDSocket = connectElgatoStreamDeckSocket;
